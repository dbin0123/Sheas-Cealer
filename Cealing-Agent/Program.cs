using Cealing_Agent.Engines;
using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? socketPath = ArgValue(args, "--socket");
        string? configPath = ArgValue(args, "--config");
        string? appDir = ArgValue(args, "--app-dir");
        string? ownerUidRaw = ArgValue(args, "--owner-uid");

        // --data-dir 是用户的可写数据目录（配置/证书/日志）。agent 以 root 运行，
        // 不能自己用 $HOME 去猜——root 的 HOME 是 /var/root，跟实际用户不是一回事。
        // 省略时退回 appDir，仅为兼容旧版 GUI 的命令行。
        string? dataDirArg = ArgValue(args, "--data-dir");

        if (socketPath is null || configPath is null || appDir is null || ownerUidRaw is null)
        {
            Console.Error.WriteLine("usage: Cealing-Agent --socket <path> --config <path> --app-dir <dir> --data-dir <dir> --owner-uid <uid>");
            return 2;
        }

        if (!int.TryParse(ownerUidRaw, out int ownerUid))
        {
            Console.Error.WriteLine($"invalid --owner-uid: {ownerUidRaw}");
            return 2;
        }

        string dataDir = dataDirArg ?? appDir;

        // 目录可能还不存在（GUI 侧一般先建好，这里兜底；GUI 以普通用户运行时也能建）
        Directory.CreateDirectory(dataDir);

        // nginx 要求 logs/ 与 temp/ 存在才能以 -p dataDir 启动
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(dataDir, "temp"));

        AgentLog.Init(AgentPaths.LogPath(dataDir));

        // 启动就把特权状态写死在日志里：以后「全局伪造没作用」不需要靠猜
        // 到底是 agent 没起来、起来了但没提权、还是提权了但被别的东西拦住。
        AgentLog.Info($"pid={Environment.ProcessId} user={Environment.UserName} elevated={Privilege.IsElevated} socket={socketPath} config={configPath}");

        try
        {
            // root 自己跑 GUI 的老用法（Windows 也是这个形态）：已经是特权进程，直接走前台服务。
            // 后台化由提权侧的 `sh -c '… </dev/null >log 2>&1 &'` 完成（见 PrivilegeEscalator），这里不关心。
            await RunAsync(socketPath, ownerUid, configPath, appDir, dataDir).ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"fatal: {ex}");
            return 1;
        }
    }

    private static async Task RunAsync(string socketPath, int ownerUid, string configPath, string appDir, string dataDir)
    {
        // 先扫上一轮的孤儿，再谈启动：上一任 agent 被 SIGKILL/崩溃带走时，它起的 mihomo/nginx
        // 还活着并占着 TUN 与 80·443，这时候新 agent 绑端口必然失败，而且报错看起来像「端口被陌生程序占用」。
        try
        {
            EngineGuard.ReapOrphans(dataDir);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"orphan engine reaping failed: {ex.Message}");
        }

        // /etc/hosts 在 macOS/Linux 上同路径；Windows 在 System32\drivers\etc\hosts
        string hostsPath = OperatingSystem.IsWindows() 
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

        EngineSupervisor supervisor = new(appDir, dataDir, ownerUid, hostsPath);

        // pid 文件只是排障线索，不是必需品：写失败（例如上一次 root 运行留下的同名文件、
        // 或临时目录权限问题）绝不能拖垮 agent，所以这里 best-effort。
        try
        {
            await File.WriteAllTextAsync(AgentPaths.PidPath, Environment.ProcessId.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not write pid file {AgentPaths.PidPath}: {ex.Message}");
        }

        await using AgentServer server = new(socketPath, ownerUid, supervisor, configPath);

        server.Start();

        // 信号清理要在等 shutdown 之前注册好：agent 是被 GUI 用 osascript/pkexec 甩到后台的，
        // `sudo kill <pid>`、systemctl stop 都会先送 SIGTERM。没有处理器的话进程是「当场死亡」，
        // hosts 里那条伪造记录和一个已经不监听的 127.0.0.1 就留在机器上 —— 用户表现为上不了网。
        using IDisposable handlers = InstallSignalHandlers(server);

        // 阻塞到收到 Shutdown 命令或进程被杀。Task.DetachAsync() 在 .NET 8 并不存在，
        // 真正让 osascript/pkexec 立刻返回的是提权那侧的 `sh -c '… </dev/null >log 2>&1 &'`
        // （见 PrivilegeEscalator.BackgroundCommand：那里刻意不用 nohup），这里的 fds 不由我们负责。
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, server.ShutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // 信号处理器里必须同步做完清理再退出：处理器一旦返回，运行时就会按默认动作把进程收掉，
    // 清理没跑完就没有第二次机会。所以这里显式 Cancel 掉默认终止、跑完清理、自己 Exit。
    private static IDisposable InstallSignalHandlers(AgentServer server)
    {
        List<IDisposable> registered = [];

        // 只接「针对这个进程的下葬指令」：SIGTERM（kill / systemctl stop）、SIGINT、SIGQUIT。
        //
        // 刻意**不接 SIGHUP**。SIGHUP 是「控制终端挂断」的会话信号，不是主人存活信号：
        // agent 由 osascript/pkexec 用 `sh -c '… &'` 甩到后台（见 PrivilegeEscalator 里
        // 「不要用 nohup」那段原因），它没有控制终端，所以能送 SIGHUP 的来源全都是会话事件
        // 而不是「GUI 没了」。把它当成退出指令，就变成了代理用着用着突然没了、
        // hosts 和根证书被顺手拆掉的线上故障。GUI 是否还活着只认主人租约那条 socket 的 EOF。
        //
        // SIGKILL 也不在列表里：它捕获不了，那种情况由 Windows Job Object 和下次启动的 pid 清扫兜底。
        foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM })
            try
            {
                IDisposable handle = PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;

                    AgentLog.Warn($"received {signal}; cleaning up before exit");

                    // 租约丢失和信号可能同时到达，RetireOnceAsync 保证只真清一次。
                    AwaitQuietly(() => server.RetireOnceAsync($"signal {signal}"), TimeSpan.FromSeconds(10));

                    // 正常退出路径靠 await using 删 socket；这里是 Environment.Exit，得自己删，
                    // 否则 GUI 会对着一个不存在的 agent 白等一轮（IsAlive 只看文件在不在）。
                    AwaitQuietly(() => server.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2));

                    Environment.Exit(0);
                });

                registered.Add(handle);
            }
            catch (PlatformNotSupportedException)
            {
                // 该平台没有这个信号（例如 Windows 上的大部分 POSIX 信号），跳过即可。
                // Windows 的关闭路径靠控制台控制事件（会映射成上面这些信号）+ 主人租约兜底。
            }

        return new SignalHandlerSet([.. registered]);
    }

    private sealed class SignalHandlerSet(IDisposable[] handlers) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable handler in handlers)
                handler.Dispose();
        }
    }

    private static void AwaitQuietly(Func<Task> action, TimeSpan timeout)
    {
        try
        {
            if (!action().Wait(timeout))
                AgentLog.Warn($"cleanup did not finish within {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            AgentLog.Error($"cleanup failed: {ex.Message}");
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, arg => arg.Equals(name, StringComparison.Ordinal));

        return index != -1 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
