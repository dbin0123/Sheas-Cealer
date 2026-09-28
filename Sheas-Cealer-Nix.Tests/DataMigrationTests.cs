using Cealing_Core;
using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _appDir;
    private readonly string _dataDir;

    public DataMigrationTests()
    {
        _appDir = Path.Combine(_root, "app");
        _dataDir = Path.Combine(_root, "data");

        Directory.CreateDirectory(_appDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private void WriteLegacy(string name, string content) =>
        File.WriteAllText(Path.Combine(_appDir, name), content);

    [Fact]
    public void MovesRuntimeStateOutOfAppBundle()
    {
        // 旧版把证书和规则写在 .app 里，升级整包替换会删掉，用户表现为「证书失信 / 规则退回 1 条」
        WriteLegacy("Cealing-Root.pem", "ROOT");
        WriteLegacy("Cealing-Key.pem", "KEY");
        WriteLegacy("Cealing-Host-U.json", "UPSTREAM");
        WriteLegacy("nginx.conf", "CONF");
        WriteLegacy("config.yaml", "MIHOMO");

        DataMigration.Migrate(_appDir, _dataDir);

        Assert.Equal("ROOT", File.ReadAllText(Path.Combine(_dataDir, "Cealing-Root.pem")));
        Assert.Equal("KEY", File.ReadAllText(Path.Combine(_dataDir, "Cealing-Key.pem")));
        Assert.Equal("UPSTREAM", File.ReadAllText(Path.Combine(_dataDir, "Cealing-Host-U.json")));
        Assert.Equal("CONF", File.ReadAllText(Path.Combine(_dataDir, "nginx.conf")));
        Assert.Equal("MIHOMO", File.ReadAllText(Path.Combine(_dataDir, "config.yaml")));
    }

    [Fact]
    public void MovesUserCreatedHostRuleFiles()
    {
        WriteLegacy("Cealing-Host-example.json", "CUSTOM");

        DataMigration.Migrate(_appDir, _dataDir);

        Assert.Equal("CUSTOM", File.ReadAllText(Path.Combine(_dataDir, "Cealing-Host-example.json")));
    }

    [Fact]
    public void DoesNotOverwriteNewerDataWithStaleBundleCopy()
    {
        // 数据目录才是唯一真相：用户改了规则后重新部署，旧 bundle 里的陈旧副本不能盖回来
        Directory.CreateDirectory(_dataDir);
        WriteLegacy("Cealing-Host-U.json", "STALE");
        File.WriteAllText(Path.Combine(_dataDir, "Cealing-Host-U.json"), "CURRENT");

        DataMigration.Migrate(_appDir, _dataDir);

        Assert.Equal("CURRENT", File.ReadAllText(Path.Combine(_dataDir, "Cealing-Host-U.json")));
    }

    [Fact]
    public void SkipsCopyWhenDataDirIsTheAppDirItself()
    {
        // 绿色版/从源码跑时数据目录可能与 bundle 同路径，此时自拷贝没有意义
        WriteLegacy("Cealing-Root.pem", "ROOT");

        DataMigration.Migrate(_dataDir, _dataDir);

        Assert.False(File.Exists(Path.Combine(_dataDir, "Cealing-Root.pem")));
    }

    [Fact]
    public void CreatesDataDirWhenMissing()
    {
        string fresh = Path.Combine(_root, "brand-new");

        DataMigration.Migrate(_appDir, fresh);

        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void DataDirLivesOutsideAppBundle()
    {
        // 回归防护：数据目录不能落在 appDir 里，否则整包替换又会连它一起删
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.NotEqual(AppPaths.Normalize(_appDir), AppPaths.Normalize(AppPaths.DataDir));
    }
}
