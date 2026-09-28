using Cealing_Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class ExternalNginxEngine(string dataDir, int ownerUid)
{
    private Process? _process;

    internal bool IsRunning => _process is { HasExited: false };

    internal async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        if (config.NginxBinaryPath is null || config.NginxConfText is null)
            throw new InvalidOperationException("external engine requires nginxBinaryPath and nginxConfText");

        cancellationToken.ThrowIfCancellationRequested();

        EnsureExecutable(config.NginxBinaryPath);

        // 配置里必须是前台模式（daemon off）：nginx 默认 fork 到后台，父进程立刻退出就拿不到句柄。
        // nginx.conf 与它引用的证书/日志/temp 都在数据目录，所以 -p 前缀也指向数据目录
        // （配置里用的是相对文件名，靠前缀解析）。
        OwnedFile.WriteAllText(Path.Combine(dataDir, "nginx.conf"), config.NginxConfText, ownerUid);

        ProcessStartInfo startInfo = new(config.NginxBinaryPath)
        {
            WorkingDirectory = dataDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        // 用 ArgumentList 而不是拼 Arguments 字符串：目录路径可能带空格。
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(dataDir);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("nginx.conf");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start nginx");

        EngineGuard.Register(dataDir, "nginx", _process, config.NginxBinaryPath);

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AgentLog.Error($"[nginx] {e.Data}");
        };

        _process.BeginErrorReadLine();

        AgentLog.Info($"external nginx started (pid {_process.Id})");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal void Stop()
    {
        Process? process = _process;

        _process = null;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"killing nginx failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();

            EngineGuard.Unregister(dataDir, "nginx");
        }
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // nginx 只认 daemon off 时的 stdout/exit，无法用端口探测判断「配置是否生效」，
    // 所以这里退化成「进程活着 + 端口在监听」两个弱信号。
    internal static bool IsListening(int port)
    {
        try
        {
            using TcpClient client = new();

            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    internal static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}
