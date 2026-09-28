using Cealing_Agent.Engines;
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

// 引擎孤儿防护。agent 被 SIGKILL/崩溃带走时不会给孩子任何信号，
// 现场就出现过 mihomo 活得比 agent 久、继续占着 TUN 和端口，
// 所以「登记 → 下次启动清扫」这条链路必须是有测试的。
public sealed class EngineGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public EngineGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RegisterThenUnregisterRoundTripsTheRecord()
    {
        using Process? sleeper = StartSleeper();

        Assert.NotNull(sleeper);

        EngineGuard.Register(_dir, "sleeper", sleeper!, PathOf(sleeper!));

        string file = Path.Combine(_dir, "engines", "sleeper.pid");

        Assert.True(File.Exists(file));
        Assert.Equal(sleeper!.Id.ToString(), File.ReadAllLines(file)[0]);

        EngineGuard.Unregister(_dir, "sleeper");

        Assert.False(File.Exists(file));

        Kill(sleeper);
    }

    [Fact]
    public void ReapKillsTheOrphanItRegistered()
    {
        Process? spawned = StartSleeper();

        Assert.NotNull(spawned);

        int pid = spawned!.Id;

        EngineGuard.Register(_dir, "sleeper", spawned, PathOf(spawned));

        spawned.Dispose();

        // 模拟「登记它的 agent 已经没了」：只剩一个还活着的 pid 和一份记录
        using Process orphan = Process.GetProcessById(pid);

        Assert.False(orphan.HasExited);

        EngineGuard.ReapOrphans(_dir);

        Assert.True(orphan.WaitForExit(5000), "登记过的孤儿必须被清掉");
        Assert.False(File.Exists(Path.Combine(_dir, "engines", "sleeper.pid")));
    }

    [Fact]
    public void ReapRefusesToKillALivePidThatIsNotTheRecordedEngine()
    {
        // pid 会被内核复用。过期记录如果指向别人的进程，按数字杀就是事故，
        // 所以名字和可执行路径都对不上时必须收手 —— 这条测试拿自己当「不该被杀的那个进程」。
        string file = Path.Combine(_dir, "engines", "ghost.pid");

        Directory.CreateDirectory(Path.Combine(_dir, "engines"));

        File.WriteAllLines(file, new[] { Environment.ProcessId.ToString(), "not-this-process", "/no/such/binary" });

        EngineGuard.ReapOrphans(_dir);

        Assert.True(Process.GetProcessById(Environment.ProcessId) is { HasExited: false });
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ReapClearsRecordsForPidsThatAreAlreadyGone()
    {
        Process? spawned = StartSleeper();

        Assert.NotNull(spawned);

        int pid = spawned!.Id;
        string binaryPath = PathOf(spawned);

        Kill(spawned);
        spawned.Dispose();

        string file = Path.Combine(_dir, "engines", "gone.pid");

        Directory.CreateDirectory(Path.Combine(_dir, "engines"));

        File.WriteAllLines(file, new[] { pid.ToString(), "gone", binaryPath });

        EngineGuard.ReapOrphans(_dir);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void RegisterBindsTheEngineToTheAgentJobOnWindows()
    {
        // Job Object 是唯一不依赖 agent 配合的机制：agent 被 SIGKILL/崩溃带走时，
        // 内核关闭 job 句柄就顺手收掉引擎，不留「等下次启动才清扫」的时间窗。
        // 绑不上只剩 warning，用户在界面上是看不出来的，所以这里必须实测。
        using Process? sleeper = StartSleeper();

        Assert.NotNull(sleeper);

        EngineGuard.Register(_dir, "sleeper", sleeper!, PathOf(sleeper!));

        bool bound = WindowsProcessJob.IsMember(sleeper!.Id);

        Kill(sleeper);

        Assert.True(bound || !OperatingSystem.IsWindows(), "引擎进程没有进 job，agent 一死就会留下孤儿");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    // 记录里的路径必须和 reaper 读到的那个一致，所以直接拿进程自己的 MainModule；
    // 读不到时退回本平台命令的常规位置。
    private static string PathOf(Process process) =>
        process.MainModule?.FileName
        ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sleep");

    // 跨平台的「还活着的长命令」：Windows 用 cmd + ping，其余用 sleep。
    private static Process? StartSleeper()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            : new ProcessStartInfo("/bin/sleep");

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("ping -n 60 127.0.0.1 > nul");
        }
        else
        {
            startInfo.ArgumentList.Add("60");
        }

        return Process.Start(startInfo);
    }
}
