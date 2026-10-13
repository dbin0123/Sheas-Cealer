using Cealing_Agent;
using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
