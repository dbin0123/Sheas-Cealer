using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sheas_Cealer_Nix.Utils;

internal static class PrivilegeEscalator
{
    internal static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    internal static string CurrentUid
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "0";

            // UID 只有是数字才可信：某些 shell 会把它设成别的东西，而 agent 对
            // --owner-uid 做 int 解析，非数字会直接让 agent 启动失败（表现为一句通用的「无法启动特权代理」）。
            return Environment.GetEnvironmentVariable("UID") is { Length: > 0 } uid && int.TryParse(uid, out _)
                ? uid
                : GetUidViaId();
        }
    }

    // osascript / pkexec 最终都要把 agent 放到后台，否则授权进程会一直挂着。
    // command 是给 shell 执行的那条命令：三个 fd 全重定向 + 结尾 & 让 shell/授权框立刻返回。
    //
    // 注意：**不要用 nohup**。osascript 的 `do shell script` 没有控制终端，BSD nohup 会在
    // ioctl(TIOCNOTTY) 失败时直接报 "nohup: can't detach from console: Inappropriate ioctl for device"
    // 并放弃执行命令，导致 agent 根本没被拉起（GUI 那边只看到通用的「无法启动特权代理」）。
    // 没有控制终端就不存在 SIGHUP 来源，`&` + fd 重定向足以让进程在 shell/osascript 退出后存活。
    private static string BackgroundCommand(string agentPath, string socketPath, string configPath, string appDir, string dataDir, string uid, string logPath) =>
        string.Join(' ',
            ShellQuote(agentPath),
            "--socket", ShellQuote(socketPath),
            "--config", ShellQuote(configPath),
            "--app-dir", ShellQuote(appDir),
            "--data-dir", ShellQuote(dataDir),
            "--owner-uid", ShellQuote(uid),
            $">{ShellQuote(logPath)} 2>&1 </dev/null &");

    private static string AppleScriptEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static IReadOnlyList<string> BuildLaunchArgs(string agentPath, string socketPath, string configPath, string appDir, string dataDir, string uid, string logPath, string platform)
    {
        return platform switch
        {
            // osascript 的 do shell script 内部就经过 /bin/sh，所以 command 里结尾的 & 直接生效，
            // 授权框点完密码后命令立刻返回。只用 AppleScriptEscape 处理双引号和反斜杠，不要再嵌套 sh -c。
            "macos" => ["osascript", "-e", $"do shell script \"{AppleScriptEscape(BackgroundCommand(agentPath, socketPath, configPath, appDir, dataDir, uid, logPath))}\" with administrator privileges"],
            // pkexec 无 shell，必须显式 sh -c 才能吃到 & 的语义。
            "linux" => ["pkexec", "sh", "-c", BackgroundCommand(agentPath, socketPath, configPath, appDir, dataDir, uid, logPath)],
            _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
        };
    }

    // 判定逻辑挪到了 Cealing-Core.Privilege：GUI 与 agent 必须用同一把尺子，
    // 否则「GUI 以为已提权、agent 其实是普通令牌」的缝隙又会变成静默失效。
    internal static bool IsElevated => Cealing_Core.Privilege.IsElevated;

    // 已经是 root（老用法 sudo 跑 GUI）时不需要提权，但也必须 nohup 后台化，
    // 否则 Launch 里的 WaitForExit(120_000) 会一直挂到 agent 退出，EnsureAgentAsync 永远等不到 socket。
    internal static IReadOnlyList<string> BuildDirectArgs(string agentPath, string socketPath, string configPath, string appDir, string dataDir, string uid, string logPath) =>
        ["sh", "-c", BackgroundCommand(agentPath, socketPath, configPath, appDir, dataDir, uid, logPath)];

    internal static void Launch(string agentPath, string socketPath, string configPath, string appDir, string dataDir, string uid, string logPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        // Windows 上没有 sh / pkexec / osascript，agent 只是同用户的普通 exe：
        // 直接进程启动，提权交给 UAC。走 Unix 分支会让 Process.Start 抛
        // 「系统找不到指定的文件」，全局伪造永远起不来。
        if (OperatingSystem.IsWindows())
        {
            LaunchWindows(agentPath, socketPath, configPath, appDir, dataDir, uid);

            return;
        }

        IReadOnlyList<string> args = IsElevated
            ? BuildDirectArgs(agentPath, socketPath, configPath, appDir, dataDir, uid, logPath)
            : BuildLaunchArgs(agentPath, socketPath, configPath, appDir, dataDir, uid, logPath, CurrentPlatform);

        ProcessStartInfo startInfo = new(args[0]) { UseShellExecute = false };

        foreach (string arg in args.Skip(1))
            startInfo.ArgumentList.Add(arg);

        using Process? process = Process.Start(startInfo);

        process?.WaitForExit(120_000);
    }

    // agent 是常驻进程，绝不能 WaitForExit（那会真的等满超时，界面表现为点了没反应）。
    // 就绪与否由 ProxyController 轮询 socket 判断。日志由 agent 自己写 AgentLog.Init 的路径，
    // 不需要 Unix 那套 shell fd 重定向。
    private static void LaunchWindows(string agentPath, string socketPath, string configPath, string appDir, string dataDir, string uid)
    {
        ProcessStartInfo startInfo = new(agentPath)
        {
            // GUI 已经是管理员：直接起，不再弹框；否则只给 agent 提权，GUI 保持普通权限运行。
            UseShellExecute = !IsElevated,
            CreateNoWindow = true
        };

        if (!IsElevated)
            startInfo.Verb = "RunAs";

        foreach (string arg in BuildWindowsArgs(socketPath, configPath, appDir, dataDir, uid))
            startInfo.ArgumentList.Add(arg);

        Process.Start(startInfo)?.Close();
    }

    internal static IReadOnlyList<string> BuildWindowsArgs(string socketPath, string configPath, string appDir, string dataDir, string uid) =>
        ["--socket", socketPath, "--config", configPath, "--app-dir", appDir, "--data-dir", dataDir, "--owner-uid", uid];

    // owner uid 必须由 GUI 报给 agent：agent 以 root 运行，它自己的 euid 不是主人。
    // 拿不到就返回 "-1"（未知），绝不能返回 "0" —— 那会让 agent 把 socket chown 给 root，
    // 普通用户权限的 GUI 连不上，只能反复再拉一个 agent 出来（进程堆叠 + 全都绑不上）。
    //
    // 用绝对路径而不是裸 `id`：macOS 从 Finder 启动的应用 PATH 很窄，
    // `Process.Start("id")` 直接抛 Win32/ENOENT，于是老代码走 catch 返回了那个致命的 "0"。
    private static string GetUidViaId()
    {
        foreach (string candidate in new[] { "/usr/bin/id", "/bin/id" })
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                using Process process = Process.Start(new ProcessStartInfo(candidate, "-u") { UseShellExecute = false, RedirectStandardOutput = true })!;
                string output = process.StandardOutput.ReadToEnd().Trim();

                if (int.TryParse(output, out int uid))
                    return uid.ToString();
            }
            catch
            {
                // 换下一个候选路径
            }
        }

        return "-1";
    }
}
