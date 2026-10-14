using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cealing_Agent;

// JVM 根本不读操作系统信任库，它只认 <java.home>/{,jre/}lib/security/cacerts。
// 实测（JDK 21 / Windows）机器级 Root 存储里 272 张根证书有 203 张不在 JVM 的受信列表里，
// 所以 TrustStore 装进系统库的 Cealing Cert Root 对一切 Java 客户端等于不存在：
// 全局伪造给它们签的叶子证书必被 JSSE 拒掉，IntelliJ IDEA 的翻译插件就表现为一句
// 没有任何细节的 "Network error"。浏览器之所以正常，是因为它带着 --ignore-certificate-errors 启动。
//
// 这里用 keytool 把同一张根证书追加进发现到的每个 cacerts，别名固定，
// 停服时按本次真正写进去的那几个库回删，卸载时按别名全量清残留。
internal static class JavaTrust
{
    internal const string Alias = "cealing-cert-root";

    private const string StorePassword = "changeit";
    private const string CacertsFileName = "cacerts";

    // Unix 上 keytool 没有扩展名，写死 .exe 会让 Linux/macOS 一个库都装不进去。
    private static string KeytoolFileName => OperatingSystem.IsWindows() ? "keytool.exe" : "keytool";

    // 一次最多处理这么多个库：扫描根里混进奇怪的目录时，宁可少装几个也不要卡住 start。
    private const int MaxStores = 64;

    // 用户手填的目录往下找 cacerts 的深度：给到 3 层是因为 IDEA 这类 IDE 的安装目录里，
    // 运行时藏在 <安装目录>/jbr 下面，而用户复制的就是安装目录那一层。
    private const int ConfiguredMaxDepth = 3;

    internal static IReadOnlyList<string> BuildImportArgs(string keytool, string cacertsPath, string rootCertPemPath) =>
        [keytool, "-importcert", "-alias", Alias, "-file", rootCertPemPath, "-keystore", cacertsPath,
            "-storepass", StorePassword, "-noprompt", "-trustcacerts"];

    // 删必须和装落在同一个库、同一个别名上，否则伪造的根永久留在别人的 JDK 里。
    internal static IReadOnlyList<string> BuildRemoveArgs(string keytool, string cacertsPath) =>
        [keytool, "-delete", "-alias", Alias, "-keystore", cacertsPath, "-storepass", StorePassword];

    // 实测 JDK 21：-list 单独就会打「证书指纹 (SHA-256):」那一行，而 -list -fingerprint 是**非法选项**
    // （keytool 回一屏用法、退出码 1）。别把 -fingerprint 加回来。
    internal static IReadOnlyList<string> BuildListArgs(string keytool, string cacertsPath) =>
        [keytool, "-list", "-alias", Alias, "-keystore", cacertsPath, "-storepass", StorePassword];

    /// <summary>
    /// 把根证书装进本机所有能写进去的 Java 信任库。返回本次依赖的库路径（回删只认这些）。
    /// </summary>
    internal static IReadOnlyList<string> Install(string rootCertPemPath, string platform, IReadOnlyList<string> userHomes, IReadOnlyList<string> configuredPaths)
    {
        List<string> installed = [];
        (string sha1, string sha256) = RootHashes(rootCertPemPath);

        foreach (string cacerts in DiscoverStores(platform, userHomes, configuredPaths))
        {
            string? keytool = FindKeytool(cacerts);

            if (keytool is null)
            {
                AgentLog.Warn($"no keytool beside {cacerts}, skipped (that JVM will keep rejecting the forged certificate)");
                continue;
            }

            try
            {
                if (AlreadyTrusted(keytool, cacerts, sha1, sha256))
                {
                    // 库里已经是这张根就一次导入都不发。重启引擎时上游路径也不会再来删一遍
                    // （见 EngineSupervisor 的 keepJavaTrust），于是 cacerts 里「暂时没有这张根」
                    // 的那个窗口消失了：实测旧做法删和装之间隔了 11 秒，正好在这个窗口里启动的
                    // JVM 读不到根证书，会一直拒绝伪造证书直到用户再重启一次 IDEA（issue #1）。
                    AgentLog.Info($"root cert already trusted in {cacerts}, import skipped");
                    installed.Add(cacerts);
                    continue;
                }

                // 别名在、但指纹不是我们这张（换过根、或上一次没删干净）：
                // -importcert 撞上同名别名会以 "Certificate already exists in this keystore" 失败，
                // 新根就永远装不进去，只能先删。
                TryRemoveAlias(keytool, cacerts);

                TrustStore.Run(BuildImportArgs(keytool, cacerts, rootCertPemPath), $"import root cert into {cacerts}");
                installed.Add(cacerts);
            }
            catch (Exception ex)
            {
                // 最常见的是用户改过该库的口令（-storepass 只认出厂默认值），
                // 其次是一个 cacerts 属于另一个 JDK 发行版只读挂载。都不该让全局伪造整体失败。
                AgentLog.Warn($"root cert import into {cacerts} failed, continuing: {ex.Message}");
            }
        }

        AgentLog.Info($"java trust stores updated: {installed.Count}");

        return installed;
    }

    private static bool AlreadyTrusted(string keytool, string cacerts, string sha1, string sha256)
    {
        try
        {
            // 别名不存在时 keytool 直接返回非 0，那正是「还没装过」。
            (int exitCode, string output) = TrustStore.Capture(BuildListArgs(keytool, cacerts), $"check root cert in {cacerts}");

            return exitCode == 0 && StoreAlreadyHasRoot(output, sha1, sha256);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"root cert check failed for {cacerts}, importing anyway: {ex.Message}");

            return false;
        }
    }

    // JDK 20 起 keytool 默认打 SHA-256，更早的版本打 SHA-1；标签和日期还是本地化的
    // （中文环境是「证书指纹 (SHA-256):」），所以两边都只留十六进制再比。
    internal static bool StoreAlreadyHasRoot(string listOutput, string sha1, string sha256)
    {
        string haystack = OnlyHexUpper(listOutput);

        return (sha1.Length != 0 && haystack.Contains(sha1)) ||
               (sha256.Length != 0 && haystack.Contains(sha256));
    }

    // 指纹只能代表「库里那张是不是我们这张」，读不出来就必须当「不是」，
    // 让调用方照常走先删再装 —— 省掉那次导入只是优化，装不上去才是故障。
    internal static (string sha1, string sha256) RootHashes(string rootCertPemPath)
    {
        try
        {
            // 读成文本再交给 CreateFromPem，别用单参数的 CreateFromPemFile(path)：net8 的 Windows 实现
            // 会顺带去找私钥，而 root.pem 按设计只有证书（私钥单独在 Cealing-Key.pem），于是它抛
            // 「The key contents do not contain a PEM...」。这一句在遍历信任库之前，抛出来整步 Java
            // 信任库一个库都走不到 —— cacerts 一张都装不进去（v1.0.10 的回归，本机日志复现）。
            using X509Certificate2 root = X509Certificate2.CreateFromPem(File.ReadAllText(rootCertPemPath));

            return (OnlyHexUpper(root.GetCertHashString()), OnlyHexUpper(root.GetCertHashString(HashAlgorithmName.SHA256)));
        }
        catch (Exception ex)
        {
            // 指纹读不出来就当成「库里不是这张」，照常先删再装：跳过那次导入只是省掉 11 秒窗口，
            // 装不进去才是故障。最常见的原因是根证书文件还没写出来（首次启动的时序）或被截断。
            AgentLog.Warn($"root cert fingerprint unavailable, importing without the skip check: {ex.Message}");

            return (string.Empty, string.Empty);
        }
    }

    private static string OnlyHexUpper(string value)
    {
        StringBuilder builder = new(value.Length);

        foreach (char c in value)
            if (Uri.IsHexDigit(c))
                builder.Append(char.ToUpperInvariant(c));

        return builder.ToString();
    }

    private static void TryRemoveAlias(string keytool, string cacerts)
    {
        try
        {
            TrustStore.Capture(BuildRemoveArgs(keytool, cacerts), $"drop stale alias in {cacerts}");
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"stale alias removal failed in {cacerts}, importing anyway: {ex.Message}");
        }
    }

    internal static void Remove(IReadOnlyList<string> cacertsPaths)
    {
        foreach (string cacerts in cacertsPaths)
            RemoveOne(cacerts);
    }

    /// <summary>
    /// 卸载善后：不记得本次装了哪些库，只能把所有库都过一遍按别名删。
    /// </summary>
    internal static void Purge(string platform, IReadOnlyList<string> userHomes, IReadOnlyList<string> configuredPaths)
    {
        foreach (string cacerts in DiscoverStores(platform, userHomes, configuredPaths))
            RemoveOne(cacerts);
    }

    private static void RemoveOne(string cacerts)
    {
        string? keytool = FindKeytool(cacerts);

        if (keytool is null)
            return;

        try
        {
            TrustStore.Run(BuildRemoveArgs(keytool, cacerts), $"remove root cert from {cacerts}");
        }
        catch (Exception ex)
        {
            // 别名本来就不在（从未导入过、或被上一次崩溃删掉了）是正常情况。
            AgentLog.Warn($"could not remove {Alias} from {cacerts}: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> DiscoverStores(string platform, IReadOnlyList<string> userHomes, IReadOnlyList<string> configuredPaths)
    {
        List<string> stores = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // 手填的排在最前面：用户显式点名的运行时不能因为自动发现先占满 MaxStores 就被挤掉。
        foreach (string cacerts in ConfiguredStores(configuredPaths).Concat(DiscoverAutoStores(platform, userHomes)))
            if (seen.Add(AppPaths.Normalize(cacerts)))
                stores.Add(cacerts);

        return stores;
    }

    internal static IEnumerable<string> DiscoverAutoStores(string platform, IReadOnlyList<string> userHomes) =>
        FindCacerts(ScanRoots(platform, userHomes));

    /// <summary>
    /// 解析设置页里手填的路径：既接受 cacerts 文件本身，也接受 JDK/JBR 家目录（含 IDE 安装目录）。
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredStores(IReadOnlyList<string> configuredPaths)
    {
        List<string> stores = [];

        foreach (string entry in configuredPaths)
        {
            string path = (entry ?? string.Empty).Trim();

            if (path.Length == 0)
                continue;

            if (File.Exists(path))
            {
                stores.Add(path);
                continue;
            }

            if (!Directory.Exists(path))
            {
                AgentLog.Warn($"configured java trust path not found, skipped: {path}");
                continue;
            }

            List<string> found = [.. FindCacerts([(path, ConfiguredMaxDepth)])];

            if (found.Count == 0)
                AgentLog.Warn($"no cacerts under configured java home, skipped: {path}");

            stores.AddRange(found);
        }

        return stores;
    }

    // 只列「确定放 JDK/JBR 的地方」，不去整盘扫。JetBrains / Android Studio 自带的运行时
    // 单独给深度：IDEA 用户要的就是它，缺了它这个功能等于没做。
    private static IEnumerable<(string Root, int Depth)> ScanRoots(string platform, IReadOnlyList<string> userHomes)
    {
        List<(string, int)> roots = [];

        void AddEnv(string name, int depth)
        {
            string? value = Environment.GetEnvironmentVariable(name);

            if (!string.IsNullOrWhiteSpace(value))
                roots.Add((value, depth));
        }

        void AddProgramFiles(string parent, string? child, int depth)
        {
            foreach (string programFiles in ProgramFilesPaths())
            {
                string path = child is null ? programFiles : Path.Combine(programFiles, parent, child);

                roots.Add((path, depth));
            }
        }

        AddEnv("JAVA_HOME", 0);

        switch (platform)
        {
            case "windows":
                AddProgramFiles("Java", null, 1);
                AddProgramFiles("Eclipse Adoptium", null, 1);
                AddProgramFiles("AdoptOpenJDK", null, 1);
                AddProgramFiles("Microsoft", null, 1);
                AddProgramFiles("Amazon Corretto", null, 1);
                AddProgramFiles("Zulu", null, 1);
                AddProgramFiles("BellSoft", null, 1);
                AddProgramFiles("Eclipse Foundation", null, 1);
                AddProgramFiles("JetBrains", null, 2);
                AddProgramFiles("Android", "Android Studio", 1);
                AddUsers(roots, userHomes, ".jdks", 1);
                AddUsers(roots, userHomes, @"AppData\Local\JetBrains", 4);
                // Toolbox 的运行时在 apps\<IDE>\<通道>\<版本>\jbr，从上面那个 JetBrains 根要翻 5 层才够，
                // 而 JetBrains 目录下还堆着缓存和配置，整棵扫太贵 —— 单给 apps 一个根，深度就能收住。
                AddUsers(roots, userHomes, @"AppData\Local\JetBrains\Toolbox\apps", 4);
                break;
            case "linux":
                AddExisting(roots, "/usr/lib/jvm", 1);
                AddExisting(roots, "/usr/java", 1);
                AddExisting(roots, "/opt/java", 1);
                AddExisting(roots, "/opt/android-studio", 1);
                AddExisting(roots, "/usr/local/android-studio", 1);
                // Homebrew 的 openjdk 是符号链接，链接目标才是真目录，所以直接写它的 libexec。
                AddExisting(roots, "/home/linuxbrew/.linuxbrew/libexec", 3);
                AddUsers(roots, userHomes, ".jdks", 1);
                AddUsers(roots, userHomes, ".local/share/JetBrains", 3);
                // Toolbox 装的 IDE 运行时在 apps/<App>/<通道>/<版本>/jbr，从 …/JetBrains 往上数层数够不着
                AddUsers(roots, userHomes, ".local/share/JetBrains/Toolbox/apps", 4);
                break;
            case "macos":
                AddExisting(roots, "/Library/Java/JavaVirtualMachines", 2);
                AddExisting(roots, "/System/Library/Java/JavaVirtualMachines", 2);
                AddExisting(roots, "/Applications", 2);
                AddExisting(roots, "/opt/homebrew/opt", 4);
                AddExisting(roots, "/usr/local/opt", 4);
                AddUsers(roots, userHomes, "Library/Java/JavaVirtualMachines", 2);
                AddUsers(roots, userHomes, "Library/Application Support/JetBrains/Toolbox/apps", 4);
                break;
            default:
                throw new PlatformNotSupportedException($"unsupported platform: {platform}");
        }

        return roots;
    }

    private static void AddExisting(List<(string Root, int Depth)> roots, string root, int depth)
    {
        if (Directory.Exists(root))
            roots.Add((root, depth));
    }

    private static void AddUsers(List<(string Root, int Depth)> roots, IReadOnlyList<string> userHomes, string relative, int depth)
    {
        foreach (string home in userHomes)
        {
            if (string.IsNullOrWhiteSpace(home))
                continue;

            string path = Path.Combine(home, relative);

            if (Directory.Exists(path))
                roots.Add((path, depth));
        }
    }

    private static IEnumerable<string> ProgramFilesPaths()
    {
        foreach (string name in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string? value = Environment.GetEnvironmentVariable(name);

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static IEnumerable<string> FindCacerts(IEnumerable<(string Root, int Depth)> roots)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        // 记录每个目录「走到它时还剩几层」，而不是简单的已访问集合：同一个目录既可能是某个扫描根本身，
        // 也可能落在另一个扫描根的子树里（`…\JetBrains` 和 `…\JetBrains\Toolbox\apps` 就是这样）。
        // 一刀切的 visited 会让浅的那次把深的那次挡掉，Toolbox 装的 IDEA 运行时正好死在这一点上。
        Dictionary<string, int> walked = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string root, int depth) in roots)
        {
            if (!Directory.Exists(root))
                continue;

            Collect(root, depth);
        }

        return found;

        void Collect(string directory, int remainingDepth)
        {
            if (found.Count >= MaxStores)
                return;

            string key = AppPaths.Normalize(directory);

            if (walked.TryGetValue(key, out int walkedDepth) && walkedDepth >= remainingDepth)
                return;

            walked[key] = remainingDepth;

            foreach (string candidate in CacertsCandidates(directory))
                if (File.Exists(candidate))
                    found.Add(candidate);

            if (remainingDepth == 0)
                return;

            IEnumerable<string> children;

            try
            {
                children = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (Exception)
            {
                // 扫描根里难免有没权限的目录（别的用户的 Homebrew 前缀之类），跳过而不是让整个 start 失败。
                return;
            }

            foreach (string child in children)
                Collect(child, remainingDepth - 1);
        }
    }

    // 一个目录既可能是 JDK 家目录（<home>/lib/security/cacerts），也可能是 JDK 8 那种带 jre 的布局，
    // 或者压根就是「装着运行时的东西」——IDE 安装目录里的 jbr、macOS 的 .app / .jdk 包。
    // 把这些形状列成候选而不是加深扫描层数：层数加深会连插件目录、缓存目录一起翻，
    // 而这几条形状覆盖了 IDEA / Android Studio / Homebrew openjdk 的全部实际装法。
    private static IEnumerable<string> CacertsCandidates(string directory) =>
    [
        Path.Combine(directory, "lib", "security", CacertsFileName),
        Path.Combine(directory, "jre", "lib", "security", CacertsFileName),
        Path.Combine(directory, "jbr", "lib", "security", CacertsFileName),
        // macOS 的 JDK 包：<name>.jdk/Contents/Home，Homebrew 的 openjdk 也是这个形状
        Path.Combine(directory, "Contents", "Home", "lib", "security", CacertsFileName),
        // IDE 的 .app 包：IntelliJ IDEA.app/Contents/jbr/Contents/Home
        Path.Combine(directory, "Contents", "jbr", "Contents", "Home", "lib", "security", CacertsFileName)
    ];

    /// <summary>
    /// cacerts 在 &lt;home&gt;/{,jre/}lib/security/ 下，keytool 在 &lt;home&gt;/bin/ 下。
    /// 从库文件往上找第一个「旁边有 bin/keytool」的祖先，比按层数硬算更能兼容各发行版的布局。
    /// </summary>
    internal static string? FindKeytool(string cacertsPath)
    {
        DirectoryInfo? directory = Directory.GetParent(cacertsPath);

        for (int i = 0; i < 5 && directory?.Parent is not null; i++, directory = directory.Parent)
        {
            string keytool = Path.Combine(directory.Parent.FullName, "bin", KeytoolFileName);

            if (File.Exists(keytool))
                return keytool;
        }

        return null;
    }
}
