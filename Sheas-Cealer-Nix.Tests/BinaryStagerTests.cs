using Sheas_Cealer_Nix.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// AppImage 这类 bundle 挂在 FUSE 上时，root（pkexec 出来的 agent）连 exec 都会被拒：
/// FUSE 没有 allow_other 时只认挂载用户。线上症状是 agent 日志里一行
/// <c>/usr/bin/sh: 1: /tmp/.mount_xxx/Cealing-Agent: Permission denied</c>，
/// 界面上则是「agent 未在超时前就绪」。这里覆盖「要不要复制」和「复制得对不对」。
/// </summary>
public class BinaryStagerTests : IDisposable
{
    private const string AppImageMountInfo =
        "812 30 0:89 / /tmp/.mount_Sheas-HBLoJO ro,nosuid,nodev,relatime - fuse.Sheas-Cealer-Nix-1.0.2-linux-x64.AppImage Sheas-Cealer-Nix-1.0.2-linux-x64.AppImage ro,nosuid,nodev,relatime,user_id=1000,group_id=1000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string MakeBundleDir(params string[] names)
    {
        string appDir = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(appDir);

        foreach (string name in names)
            File.WriteAllText(Path.Combine(appDir, name), "content-of-" + name);

        return appDir;
    }

    // 判定：bundle 落在无 allow_other 的 FUSE 上就必须暂存
    [Fact]
    public void AppImageMountWithoutAllowOtherNeedsStaging() =>
        Assert.True(BinaryStager.IsRootBlockedBundle("/tmp/.mount_Sheas-HBLoJO", [AppImageMountInfo]));

    [Fact]
    public void MountThatAllowsOtherUsersIsAccessibleToRoot()
    {
        string line = AppImageMountInfo.Replace("group_id=1000", "group_id=1000,allow_other");

        Assert.False(BinaryStager.IsRootBlockedBundle("/tmp/.mount_Sheas-HBLoJO", [line]));
    }

    // allow_other 记在挂载选项（而不是超级块选项）上时同样算可达
    [Fact]
    public void AllowOtherInMountOptionsIsHonoured()
    {
        string line = AppImageMountInfo.Replace("nodev,relatime -", "nodev,relatime,allow_other -");

        Assert.False(BinaryStager.IsRootBlockedBundle("/tmp/.mount_Sheas-HBLoJO", [line]));
    }

    [Fact]
    public void PlainDirectoryOnDiskNeedsNoStaging()
    {
        IEnumerable<string> mounts =
        [
            "22 30 8:2 / / rw,relatime - ext4 /dev/sda2 rw,relatime",
            "346 32 0:67 / /run/user/1000 rw,nosuid,nodev,relatime - tmpfs tmpfs rw,size=1000k"
        ];

        Assert.False(BinaryStager.IsRootBlockedBundle("/opt/Sheas-Cealer-Nix", mounts));
        Assert.False(BinaryStager.IsRootBlockedBundle(Path.Combine(Path.GetTempPath(), "squashfs-root"), mounts));
    }

    // 底层 / 是 ext4、bundle 挂在 fuse 上：必须取最深的那条挂载点，否则判定会被底层覆盖掉
    [Fact]
    public void DeepestMountPointWins()
    {
        IEnumerable<string> mounts =
        [
            "22 30 8:2 / / rw,relatime - ext4 /dev/sda2 rw,relatime",
            AppImageMountInfo
        ];

        Assert.True(BinaryStager.IsRootBlockedBundle("/tmp/.mount_Sheas-HBLoJO", mounts));
    }

    // 挂载表里的空格是八进制转义的，不还原就没法和真实路径比对
    [Fact]
    public void EscapedMountPointIsUnescaped()
    {
        string line = AppImageMountInfo.Replace("/tmp/.mount_Sheas-HBLoJO", "/tmp/my\\040mount/Sheas");

        Assert.True(BinaryStager.IsRootBlockedBundle("/tmp/my mount/Sheas", [line]));
    }

    [Fact]
    public void MalformedMountInfoLineIsIgnored() =>
        Assert.False(BinaryStager.IsRootBlockedBundle("/tmp/.mount_Sheas-HBLoJO", ["garbage without separator"]));

    [Fact]
    public void StagesExecutableFilesAndSkipsDebugSymbols()
    {
        string appDir = MakeBundleDir("Cealing-Agent", "Cealing-Agent.dll", "libcoreclr.so", "Cealing-Agent.pdb");
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        BinaryStager.StageResult result = BinaryStager.StageBundle(appDir, stagedDir);

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(stagedDir, "Cealing-Agent")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "Cealing-Agent.dll")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "libcoreclr.so")));
        // 调试符号不参与执行，几百 MB 的 bundle 里能省一点是一点
        Assert.False(File.Exists(Path.Combine(stagedDir, "Cealing-Agent.pdb")));
    }

    [Fact]
    public void StagesNestedFiles()
    {
        string appDir = MakeBundleDir("Cealing-Agent");
        Directory.CreateDirectory(Path.Combine(appDir, "runtimes", "native"));
        File.WriteAllText(Path.Combine(appDir, "runtimes", "native", "libx.so"), "so");
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);
        Assert.True(File.Exists(Path.Combine(stagedDir, "runtimes", "native", "libx.so")));
    }

    [Fact]
    public void DoesNotChokeOnSymlinks()
    {
        string appDir = MakeBundleDir("Cealing-Agent");

        try
        {
            // AppImage 里的 .DirIcon 就是软链，断链时 File.Copy 会直接抛
            File.CreateSymbolicLink(Path.Combine(appDir, ".DirIcon"), Path.Combine(appDir, "missing.png"));
        }
        catch (Exception)
        {
            return; // Windows 上没权限建软链，跳过这个场景
        }

        Assert.True(BinaryStager.StageBundle(appDir, Path.Combine(_root, "bin", "1.0.2")).Ok);
    }

    [Fact]
    public void ExecutableFilesKeepExecBit()
    {
        if (OperatingSystem.IsWindows())
            return;

        string appDir = MakeBundleDir("Cealing-Agent", "Cealing-Mihomo", "createdump", "libcoreclr.so", "cealing-proxy.json");
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);

        // File.Copy 不保留 mode，副本按 umask 落成 0644 的话 root 照样 exec 不了
        foreach (string name in new[] { "Cealing-Agent", "Cealing-Mihomo", "createdump", "libcoreclr.so" })
            Assert.True(File.GetUnixFileMode(Path.Combine(stagedDir, name)).HasFlag(UnixFileMode.UserExecute), name);

        Assert.False(File.GetUnixFileMode(Path.Combine(stagedDir, "cealing-proxy.json")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void SecondStageOfSameBundleSkipsTheCopy()
    {
        string appDir = MakeBundleDir("Cealing-Agent", "Cealing-Agent.dll");
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);

        File.Delete(Path.Combine(stagedDir, "Cealing-Agent.dll"));

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);
        // 同一个 bundle：靠标记文件判定，不再搬第二遍
        Assert.False(File.Exists(Path.Combine(stagedDir, "Cealing-Agent.dll")));
    }

    [Fact]
    public void RebuiltBundleOfTheSameVersionIsRecopied()
    {
        string appDir = MakeBundleDir("Cealing-Agent", "Cealing-Agent.dll");
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);
        File.Delete(Path.Combine(stagedDir, "Cealing-Agent.dll"));

        // 开发机反复打包时版本号不变，但 agent 二进制变了 —— 必须重新复制
        File.WriteAllText(Path.Combine(appDir, "Cealing-Agent"), "a-different-and-longer-binary");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);
        Assert.True(File.Exists(Path.Combine(stagedDir, "Cealing-Agent.dll")));
    }

    [Fact]
    public void StagingPrunesOlderVersionDirectories()
    {
        string appDir = MakeBundleDir("Cealing-Agent");
        string staleDir = Path.Combine(_root, "bin", "1.0.1");
        Directory.CreateDirectory(Path.Combine(staleDir, "leftover"));
        string stagedDir = Path.Combine(_root, "bin", "1.0.2");

        Assert.True(BinaryStager.StageBundle(appDir, stagedDir).Ok);
        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public void ReportsFailureWhenTargetCannotBeWritten()
    {
        string appDir = MakeBundleDir("Cealing-Agent");
        // 拿一个文件当「目录」用，创建目录必然失败
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");

        BinaryStager.StageResult result = BinaryStager.StageBundle(appDir, Path.Combine(blocked, "bin", "1.0.2"));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
