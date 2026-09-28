using Cealing_Core;
using System;
using System.Diagnostics;
using System.IO;

namespace Cealing_Agent.Engines;

internal sealed class MihomoEngine(string dataDir, int ownerUid)
{
    private Process? _process;

    internal bool IsRunning => _process is { HasExited: false };

    internal void Start(ProxyConfig config)
    {
        if (config.MihomoBinaryPath is null || config.MihomoConfText is null)
            throw new InvalidOperationException("mihomo engine requires mihomoBinaryPath and mihomoConfText");

        ExternalNginxEngine.EnsureExecutable(config.MihomoBinaryPath);

        // config.yaml 是运行时改写的文件，放数据目录而不是 app bundle
        OwnedFile.WriteAllText(Path.Combine(dataDir, "config.yaml"), config.MihomoConfText, ownerUid);

        ProcessStartInfo startInfo = new(config.MihomoBinaryPath)
        {
            WorkingDirectory = dataDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(dataDir);

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start mihomo");

        EngineGuard.Register(dataDir, "mihomo", _process, config.MihomoBinaryPath);

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AgentLog.Error($"[mihomo] {e.Data}");
        };

        _process.BeginErrorReadLine();

        AgentLog.Info($"mihomo started (pid {_process.Id})");
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
            AgentLog.Warn($"killing mihomo failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();

            // 哪怕 Kill 失败也要销账：留着记录只会让下次启动去误伤一个陌生进程。
            // 真正没杀掉的孤儿由 Job Object（Windows）兜底。
            EngineGuard.Unregister(dataDir, "mihomo");
        }
    }
}
