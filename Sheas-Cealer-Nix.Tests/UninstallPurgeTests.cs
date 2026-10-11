using Cealing_Agent;
using Cealing_Core;
using System;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// 卸载善后的用户侧部分：暂存二进制 / 数据目录 / 开机自启条目。
/// 刻意不跑 <see cref="Uninstaller.Run"/> —— 那会真的动系统信任库，测试没有资格碰机器级状态。
/// </summary>
public sealed class UninstallPurgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cealing-uninstall-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录留给系统回收
        }
    }

    private string SandboxHome()
    {
        string home = Path.Combine(_root, "home");
        string staged = AppPaths.StagedBinRootForHome(home);
        string dataDir = AppPaths.DataDirForHome(home);

        // AppPaths 优先读 XDG_*，那两个变量一旦指向沙箱外面，下面的删除就打的是真实的用户目录。
        // 宁可让测试红，也不能拿机器上的东西做实验。
        Assert.StartsWith(_root, Path.GetFullPath(staged));
        Assert.StartsWith(_root, Path.GetFullPath(dataDir));

        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(Path.Combine(staged, "Cealing-Agent"), "staged binary");
        File.WriteAllText(Path.Combine(dataDir, "config.yaml"), "user rules");

        return home;
    }

    [Fact]
    public void PurgeUserFilesAlwaysRemovesStagedBinaries()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows 上没有暂存这套机制，AppPaths.DeleteStagedBin 在那里直接返回

        string home = SandboxHome();

        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: false);

        Assert.False(Directory.Exists(AppPaths.StagedBinRootForHome(home)));
    }

    [Fact]
    public void PurgeUserFilesKeepsUserDataUnlessPurging()
    {
        string home = SandboxHome();

        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: false);

        Assert.True(File.Exists(Path.Combine(AppPaths.DataDirForHome(home), "config.yaml")));

        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: true);

        Assert.False(Directory.Exists(AppPaths.DataDirForHome(home)));
    }

    // 装（GUI 的 AutoStartManager）和删（agent 的卸载善后）必须是同一个位置，
    // 否则卸载就把一条指向已删除文件的自启条目留在机器上。
    [Fact]
    public void AutostartEntryPathMatchesTheGuiWriter()
    {
        if (OperatingSystem.IsWindows())
            return;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string writtenByGui = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "autostart",
            "sheas-cealer-nix.desktop");

        if (OperatingSystem.IsMacOS())
            // macOS 的条目在 LaunchAgents，LocalApplicationData 那条只对 Linux 成立。
            writtenByGui = Path.Combine(home, "Library", "LaunchAgents", "io.github.projectsheascealer.nix.plist");

        Assert.Equal(writtenByGui, AppPaths.AutostartEntryForHome(home));
    }

    [Fact]
    public void PurgeUserFilesRemovesAutostartEntry()
    {
        string home = SandboxHome();

        if (OperatingSystem.IsWindows())
            // Windows 的自启条目在 HKCU 注册表里，不是文件，这里只测文件形态的两个平台。
            return;

        string entry = AppPaths.AutostartEntryForHome(home);

        Assert.StartsWith(_root, Path.GetFullPath(entry));

        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        File.WriteAllText(entry, "[Desktop Entry]");

        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: false);

        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void PurgeUserFilesIsIdempotent()
    {
        string home = SandboxHome();

        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: true);
        Uninstaller.PurgeUserFiles(home, ownerUid: -1, purgeUserData: true);
    }

    // --user-home 传错（指向别人的目录，或少了最后一级）时必须拒绝，而不是连锅端走。
    [Fact]
    public void DeleteAppFolderRefusesAnythingThatIsNotOurFolder()
    {
        string foreign = Path.Combine(_root, "not-our-folder");

        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep-me"), "data");

        Assert.Throws<ArgumentException>(() => AppPaths.DeleteAppFolder(foreign));
        Assert.Throws<ArgumentException>(() => AppPaths.DeleteAppFolder(_root));
        Assert.True(File.Exists(Path.Combine(foreign, "keep-me")));

        string ours = Path.Combine(foreign, AppPaths.AppFolderName);

        Directory.CreateDirectory(ours);
        AppPaths.DeleteAppFolder(ours);

        Assert.False(Directory.Exists(ours));
    }

    // macOS 的暂存目录嵌在数据目录里：清暂存时连数据目录一起删，等于卸载顺手毁掉用户的规则。
    [Fact]
    public void DeleteStagedBinLeavesTheDataDirAlone()
    {
        string home = SandboxHome();

        AppPaths.DeleteStagedBin(home);

        Assert.False(Directory.Exists(AppPaths.StagedBinRootForHome(home)));
        Assert.True(File.Exists(Path.Combine(AppPaths.DataDirForHome(home), "config.yaml")));
    }

    [Fact]
    public void StopLiveAgentIgnoresAnUnrelatedPid()
    {
        string pidPath = Path.Combine(_root, "cealing-agent.pid");

        // init 一定活着，但名字绝不是 Cealing-Agent：按进程名核对就必须放过它。
        File.WriteAllText(pidPath, "1");

        Uninstaller.StopLiveAgent(pidPath);

        Assert.True(File.Exists(pidPath));
    }

    // 常驻 agent 处理 uninstall 命令时，pid 文件里记的就是它自己：不能被这一步杀掉。
    [Fact]
    public void StopLiveAgentNeverKillsItsOwnProcess()
    {
        string pidPath = Path.Combine(_root, "cealing-agent.pid");

        File.WriteAllText(pidPath, Environment.ProcessId.ToString());

        Uninstaller.StopLiveAgent(pidPath);

        Assert.True(File.Exists(pidPath));
    }

    [Fact]
    public void StopLiveAgentIgnoresGarbagePidFiles()
    {
        string pidPath = Path.Combine(_root, "cealing-agent.pid");

        File.WriteAllText(pidPath, "not-a-pid");

        Uninstaller.StopLiveAgent(pidPath);
        Uninstaller.StopLiveAgent(Path.Combine(_root, "missing.pid"));
    }

    [Fact]
    public void RemoveRuntimeFilesDeletesSocketAndPidAndToleratesMissing()
    {
        string socket = Path.Combine(_root, "cealing-agent.sock");
        string pid = Path.Combine(_root, "cealing-agent.pid");

        File.WriteAllText(socket, string.Empty);
        File.WriteAllText(pid, "4242");

        Uninstaller.RemoveRuntimeFiles(socket, pid);

        Assert.False(File.Exists(socket));
        Assert.False(File.Exists(pid));

        // 第二次（socket 和 pid 都没了）不能抛：卸载脚本会重复调用。
        Uninstaller.RemoveRuntimeFiles(socket, pid);
    }

    // socket / pid 路径只能来自 Options：默认值是 /tmp 上那个活的 agent 在用的一切。
    [Fact]
    public void UninstallerTakesRuntimePathsFromOptionsNotDefaults()
    {
        string sock = Path.Combine(_root, "unused.sock");
        string pid = Path.Combine(_root, "unused.pid");

        File.WriteAllText(Path.Combine(_root, "sentinel"), "keep");

        Uninstaller.Options options = new(
            HostsPath: Path.Combine(_root, "absent-hosts"),
            DataDir: null,
            OwnerUid: -1,
            UserHomes: [],
            PurgeUserData: false,
            SocketPath: sock,
            PidPath: pid);

        Assert.Equal(sock, options.SocketPath);
        Assert.NotEqual(AgentPaths.SocketPath, options.SocketPath);
        Assert.NotEqual(AgentPaths.PidPath, options.PidPath);

        Assert.True(File.Exists(Path.Combine(_root, "sentinel")));
    }
}
