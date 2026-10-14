using Cealing_Agent;
using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// Java 信任库（cacerts）的定位：自动发现之外，用户还能在设置页手填路径。
/// </summary>
/// <remarks>
/// 只断言「找到哪些库、用什么命令去装」，不真跑 keytool —— 那需要一台装好 JDK 的机器，
/// 而且会改写别人电脑上的信任库。
/// </remarks>
public sealed class JavaTrustTests : IDisposable
{
    // keytool 在 Unix 上没有扩展名，测试造的文件名必须和生产侧同一套判断。
    private static readonly string KeytoolName = OperatingSystem.IsWindows() ? "keytool.exe" : "keytool";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cealing-java-trust-" + Path.GetRandomFileName());

    public JavaTrustTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ImportArgsCarryTheFixedAliasAndDefaultPassword()
    {
        IReadOnlyList<string> args = JavaTrust.BuildImportArgs("/opt/jdk/bin/keytool", "/opt/jdk/lib/security/cacerts", "/data/Cealing-Root.pem");

        Assert.Equal("/opt/jdk/bin/keytool", args[0]);
        Assert.Equal("-importcert", args[1]);
        Assert.Equal(JavaTrust.Alias, ArgsValue(args, "-alias"));
        Assert.Equal("/opt/jdk/lib/security/cacerts", ArgsValue(args, "-keystore"));
        Assert.Equal("/data/Cealing-Root.pem", ArgsValue(args, "-file"));

        // cacerts 的出厂口令就是 changeit；用户改过口令会在 Install 那步失败并留下日志。
        Assert.Equal("changeit", ArgsValue(args, "-storepass"));
        Assert.Equal("-noprompt", args[^2]);
        Assert.Equal("-trustcacerts", args[^1]);
    }

    [Fact]
    public void RemoveArgsHitTheSameAliasAndStoreAsImport()
    {
        IReadOnlyList<string> import = JavaTrust.BuildImportArgs("keytool", "/opt/jdk/lib/security/cacerts", "/data/root.pem");
        IReadOnlyList<string> remove = JavaTrust.BuildRemoveArgs("keytool", "/opt/jdk/lib/security/cacerts");

        // 删错别名或错库，等于把伪造的根永久留在别人的 JDK 里。
        Assert.Equal(JavaTrust.Alias, ArgsValue(remove, "-alias"));
        Assert.Equal(ArgsValue(import, "-keystore"), ArgsValue(remove, "-keystore"));
        Assert.Equal(ArgsValue(import, "-storepass"), ArgsValue(remove, "-storepass"));
    }

    [Fact]
    public void ListArgsTargetTheSameAliasAndStoreAsImport()
    {
        IReadOnlyList<string> import = JavaTrust.BuildImportArgs("keytool", "/opt/jdk/lib/security/cacerts", "/data/root.pem");
        IReadOnlyList<string> list = JavaTrust.BuildListArgs("keytool", "/opt/jdk/lib/security/cacerts");

        // 比对用的必须是同一个别名和同一个库，否则「已经装过」的判断会落在别处。
        Assert.Equal("-list", list[1]);
        Assert.Equal(JavaTrust.Alias, ArgsValue(list, "-alias"));
        Assert.Equal(ArgsValue(import, "-keystore"), ArgsValue(list, "-keystore"));
        Assert.Equal(ArgsValue(import, "-storepass"), ArgsValue(list, "-storepass"));

        // 实测 keytool 的 -list 不认 -fingerprint（非法选项，退出码 1），而 -list 本身就打指纹行。
        Assert.DoesNotContain("-fingerprint", list);
    }

    [Fact]
    public void StoreFingerprintMatchAcceptsBothHashFormsAndLabels()
    {
        const string Sha1 = "DBE3FDEB4C5F102033E31C3ACCE28576AA2CE8DE";
        const string Sha256 = "7B12A3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F80";

        // JDK 20 之前打 SHA-1、之后打 SHA-256，标签还跟着本地化走（中文环境是「证书指纹」）。
        // 这两段就是本机 JDK 21 上 -list 的真实输出形状。
        Assert.True(JavaTrust.StoreAlreadyHasRoot(
            $"cealing-cert-root, 2026-10-14 上午9:11, trustedCertEntry,{Environment.NewLine}" +
            $"Certificate fingerprint (SHA1): {Coloned(Sha1)}{Environment.NewLine}", Sha1, Sha256));

        Assert.True(JavaTrust.StoreAlreadyHasRoot(
            $"cealing-cert-root, 2026年10月14日, trustedCertEntry,{Environment.NewLine}" +
            $"证书指纹 (SHA-256): {Coloned(Sha256)}{Environment.NewLine}", Sha1, Sha256));

        // 库里是上一任的根：绝不能当成「已经可信」，否则新根永远装不进去。
        Assert.False(JavaTrust.StoreAlreadyHasRoot(
            $"cealing-cert-root, 2026-10-14, trustedCertEntry,{Environment.NewLine}" +
            $"Certificate fingerprint (SHA1): {Coloned("F" + Sha1[1..])}{Environment.NewLine}", Sha1, Sha256));

        Assert.False(JavaTrust.StoreAlreadyHasRoot(string.Empty, Sha1, Sha256));

        // 指纹读不出来时传的是两个空串，此时必须判为「不可信」去走导入，
        // 而不是把满屏十六进制的输出当成匹配。
        Assert.False(JavaTrust.StoreAlreadyHasRoot($"证书指纹 (SHA-256): {Coloned(Sha256)}", string.Empty, string.Empty));
    }

    [Fact]
    public void RootHashesMatchTheFingerprintsKeytoolPrints()
    {
        // 按生产侧的写法原样落盘：ProxyCertificateFactory 写的是 root.ExportCertificatePem()
        // —— UTF8 无 BOM、**不带结尾换行**、且里面只有证书（私钥单独在 Cealing-Key.pem）。
        // v1.0.10 在这份字节上炸了：net8 的 Windows 里单参数 CreateFromPemFile 会连带去找私钥，
        // 找不到就抛，于是 Java 信任库整步被跳过（本机 cealing-agent.log 复现）。
        string path = WriteRootPem(out byte[] der);

        (string sha1, string sha256) = JavaTrust.RootHashes(path);

        Assert.Equal(Convert.ToHexString(SHA1.HashData(der)), sha1);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(der)), sha256);

        // keytool 打的是带冒号的同一种十六进制，比对用的就是这两串。
        Assert.True(JavaTrust.StoreAlreadyHasRoot($"证书指纹 (SHA-256): {Coloned(sha256)}", sha1, sha256));
    }

    [Fact]
    public void RootHashesYieldEmptyHashesWhenThePemCarriesNoCertificate()
    {
        // 只有私钥、没有证书块：读不出指纹，但不能把整步 Java 信任库带崩。
        using RSA strayKey = RSA.Create(2048);
        string path = Path.Combine(_root, "no-certificate.pem");

        File.WriteAllText(path, strayKey.ExportPkcs8PrivateKeyPem(), Utf8NoBom);

        (string sha1, string sha256) = JavaTrust.RootHashes(path);

        Assert.Equal(string.Empty, sha1);
        Assert.Equal(string.Empty, sha256);
    }

    [Fact]
    public void RootHashesYieldEmptyHashesWhenTheRootPemIsMissing()
    {
        // 根证书文件还没写出来（首次启动的时序）时同样只能退回「照常导入」。
        (string sha1, string sha256) = JavaTrust.RootHashes(Path.Combine(_root, "does-not-exist.pem"));

        Assert.Equal(string.Empty, sha1);
        Assert.Equal(string.Empty, sha256);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 造一张和生产侧同规格的自签根（CN 与基本约束都照抄），返回落盘路径和它的 DER。
    private string WriteRootPem(out byte[] der)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(TrustStore.RootCertSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, false));

        using X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(100));
        string path = Path.Combine(_root, "Cealing-Root.pem");

        File.WriteAllText(path, root.ExportCertificatePem(), Utf8NoBom);

        der = root.RawData;

        return path;
    }

    // keytool 打的指纹带冒号分隔，库里存的就长这样。
    private static string Coloned(string hex) => string.Join(':', hex);

    [Fact]
    public void ConfiguredCacertsFileIsTakenAsIs()
    {
        string cacerts = Write("custom", "security", "cacerts");

        Assert.Equal(cacerts, Assert.Single(JavaTrust.ConfiguredStores([cacerts])));
    }

    [Fact]
    public void ConfiguredJavaHomeIsSearchedForCacerts()
    {
        string home = MakeJavaHome("jdk-21");

        Assert.Equal(
            Path.Combine(home, "lib", "security", "cacerts"),
            Assert.Single(JavaTrust.ConfiguredStores([home])));
    }

    [Fact]
    public void ConfiguredJavaHomeAlsoAcceptsTheJreLayout()
    {
        // JDK 8 把运行时放在 <home>/jre 下面。
        string home = Path.Combine(_root, "jdk-8");
        string cacerts = Write("jdk-8", "jre", "lib", "security", "cacerts");

        Assert.Equal(cacerts, Assert.Single(JavaTrust.ConfiguredStores([home])));
    }

    [Fact]
    public void ConfiguredIdeDirectoryFindsItsBundledRuntime()
    {
        // 用户复制过来的是 IDEA 的安装目录，运行时藏在里面的 jbr 那一层 ——
        // 这正是自动发现够不着、要手填的典型场景（issue #1）。
        string ideDir = Path.Combine(_root, "IntelliJ IDEA");
        string cacerts = Write("IntelliJ IDEA", "jbr", "lib", "security", "cacerts");

        Assert.Equal(cacerts, Assert.Single(JavaTrust.ConfiguredStores([ideDir])));
    }

    [Fact]
    public void ConfiguredPathsAreTrimmedAndMissingOnesSkipped()
    {
        string home = MakeJavaHome("jdk-17");

        IReadOnlyList<string> stores = JavaTrust.ConfiguredStores(
            ["  ", $"{home}  ", Path.Combine(_root, "does-not-exist")]);

        Assert.Equal(Path.Combine(home, "lib", "security", "cacerts"), Assert.Single(stores));
    }

    [Fact]
    public void DiscoverStoresKeepsConfiguredOnesFirstAndDropsDuplicates()
    {
        string home = MakeJavaHome("jdk-dedupe");
        string cacerts = Path.Combine(home, "lib", "security", "cacerts");

        // 同一个库被指了两次：一次直接给 cacerts 文件，一次给它的 JDK 家目录。
        // 手填的必须排最前（自动发现有 MaxStores 上限，显式点名的不能被挤掉），而且只能留一份。
        IReadOnlyList<string> stores = JavaTrust.DiscoverStores("linux", [], [cacerts, home]);

        Assert.Equal(cacerts, stores[0]);
        Assert.Equal(stores.Count, stores.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FindKeytoolWalksUpFromCacertsToTheJavaHome()
    {
        string home = MakeJavaHome("jdk-keytool");
        string cacerts = Path.Combine(home, "lib", "security", "cacerts");

        Write("jdk-keytool", "bin", KeytoolName);

        Assert.Equal(Path.Combine(home, "bin", KeytoolName), JavaTrust.FindKeytool(cacerts));
    }

    [Fact]
    public void FindKeytoolReturnsNullWhenTheRuntimeShipsNoKeytool()
    {
        // 只装了 JRE 的机器（或被裁过的运行时）没有 keytool，导入这件事本身做不到。
        string home = MakeJavaHome("jre-only");

        Assert.Null(JavaTrust.FindKeytool(Path.Combine(home, "lib", "security", "cacerts")));
    }

    [Fact]
    public void AutoDiscoveryFindsToolboxInstalledIdeRuntime()
    {
        // Toolbox 的运行时在 apps/<IDE>/<通道>/<版本>/jbr，比 Program Files 那种装法深一层，
        // 单靠「JetBrains 目录 + 固定层数」数不到 —— 而 issue #1 报的就是 IDEA。
        string apps = Path.Combine(_root, @"AppData\Local\JetBrains\Toolbox\apps");
        string cacerts = Path.Combine(apps, "IntelliJIdea", "ch-0", "241.14494.150", "jbr", "lib", "security", "cacerts");

        WriteAbsolute(cacerts);

        Assert.Contains(Normalize(cacerts), JavaTrust.DiscoverAutoStores("windows", [_root]).Select(Normalize));
    }

    [Fact]
    public void AutoDiscoveryFindsMacStyleJdkBundle()
    {
        // macOS 的 .jdk 包把家目录放在 Contents/Home 下面
        string jvms = Path.Combine(_root, "Library/Java/JavaVirtualMachines");
        string cacerts = Path.Combine(jvms, "temurin-21.jdk", "Contents", "Home", "lib", "security", "cacerts");

        WriteAbsolute(cacerts);

        Assert.Contains(Normalize(cacerts), JavaTrust.DiscoverAutoStores("macos", [_root]).Select(Normalize));
    }

    [Fact]
    public void ConfiguredMacAppBundleIsSearchedInsideContents()
    {
        // 用户在设置页填的往往就是 IDE 那一层 .app
        string appDir = Path.Combine(_root, "IntelliJ IDEA.app");
        string cacerts = Path.Combine(appDir, "Contents", "jbr", "Contents", "Home", "lib", "security", "cacerts");

        WriteAbsolute(cacerts);

        Assert.Equal(
            Normalize(cacerts),
            Normalize(Assert.Single(JavaTrust.ConfiguredStores([appDir]))));
    }

    private string MakeJavaHome(params string[] name)
    {
        string[] cacertsParts = [.. name, "lib", "security", "cacerts"];

        Write(cacertsParts);

        return Path.Combine(_root, Path.Combine(name));
    }

    private string Write(params string[] parts) => WriteAbsolute(Path.Combine(_root, Path.Combine(parts)));

    private static string WriteAbsolute(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);

        return path;
    }

    // 生产侧用 Path.Combine 拼出的路径可能混着两种分隔符（设置页里的相对片段就是 "Library/Java/..." 这种写法），
    // 断言前两边都归一化，才不把测试绑死在某一个平台上。
    private static string Normalize(string path) => AppPaths.Normalize(path);

    private static string? ArgsValue(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == name)
                return args[i + 1];

        return null;
    }
}
