using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Cealing_Agent;

internal static class TrustStore
{
    internal const string RootCertSubjectName = "CN=Cealing Cert Root";
    internal const string ChildCertSubjectName = "CN=Cealing Cert Child";

    private const string MacSystemKeychain = "/Library/Keychains/System.keychain";
    private const string LinuxCertDir = "/usr/local/share/ca-certificates";
    private const string LinuxCertFile = "cealing-root.crt";
    // certutil 的存储名就是裸的 `Root`，而且默认操作机器级存储（切换到用户级要显式加 -user）。
    // 写成 PowerShell 的 `LocalMachine\Root` 路径语法，certutil 会把它当成"文件路径"去打开，
    // 直接失败：CertUtil: -addstore 失败: 0x80070035 (WIN32: 53 ERROR_BAD_NETPATH)。
    // 装和删必须用同一个常量，否则证书删不掉、永久残留在系统信任库里。
    private const string WindowsCertStore = "Root";

    internal static IReadOnlyList<string> BuildInstallArgs(string rootCertPemPath, string platform) => platform switch
    {
        "macos" => ["security", "add-trusted-cert", "-d", "-r", "trustRoot", "-k", MacSystemKeychain, rootCertPemPath],
        "linux" => ["sh", "-c", $"cp -- {ShellQuote(rootCertPemPath)} {ShellQuote(LinuxCertDir + "/" + LinuxCertFile)} && update-ca-certificates"],
        "windows" => ["certutil", "-addstore", WindowsCertStore, rootCertPemPath],
        _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
    };

    internal static IReadOnlyList<string> BuildUninstallArgs(string thumbprint, string platform) => platform switch
    {
        "macos" => ["security", "delete-certificate", "-Z", thumbprint, MacSystemKeychain],
        "linux" => ["sh", "-c", $"rm -f -- {ShellQuote(LinuxCertDir + "/" + LinuxCertFile)} && update-ca-certificates --fresh"],
        "windows" => ["certutil", "-delstore", WindowsCertStore, thumbprint],
        _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
    };

    // appDir 来自 ApplicationBase，理论上可以包含单引号（应用装在 ~/Applications/My 'App'/ 这类路径）。
    // 直接插进 sh -c 会被 shell 拆开，属于命令注入，所以统一走单引号转义。
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static string Thumbprint(X509Certificate2 certificate) => certificate.Thumbprint;

    // macOS 上**不要**由 agent 装系统域根证书：`security add-trusted-cert -d` 即使以 root 运行
    // 也一定会弹管理员授权框，agent 是无头进程，用户看不到也点不了，WaitForExit 会永久阻塞，
    // 表现为「点任何按钮都卡住、start 无响应」。macOS 的信任安装由 GUI 的 UserTrust 负责
    // （用户身份 + osascript 授权）。Linux 的 NSS 信任只有 root 能写，仍由 agent 做。
    // Windows 上 agent 以管理员身份运行，可以直接用 certutil 安装到系统信任库。
    internal static void Install(string rootCertPemPath, string platform)
    {
        if (platform == "macos")
        {
            AgentLog.Info("skip system-domain root cert install on macos (handled by GUI UserTrust)");
            return;
        }

        Run(BuildInstallArgs(rootCertPemPath, platform), "install root cert");
    }

    // macOS 上系统域的卸载同样需要管理员权限，无头 agent 里会弹框/阻塞。
    // 反正 Install 阶段已不再往系统域装，这一步直接跳过即可。
    internal static void Uninstall(string thumbprint, string platform)
    {
        if (platform == "macos")
            return;

        try
        {
            Run(BuildUninstallArgs(thumbprint, platform), "uninstall root cert");
        }
        catch (Exception ex)
        {
            // 证书本来就不在（首次启动、或上次卸载失败）不算错误
            AgentLog.Warn($"uninstall root cert skipped: {ex.Message}");
        }
    }

    private static void Run(IReadOnlyList<string> args, string what)
    {
        AgentLog.Info($"{what}: {string.Join(' ', args)}");

        ProcessStartInfo startInfo = new(args[0]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };

        foreach (string arg in args.Skip(1))
            startInfo.ArgumentList.Add(arg);

        using Process? process = Process.Start(startInfo);

        if (process is null)
            throw new InvalidOperationException($"failed to start {args[0]} for {what}");

        // 必须先 WaitForExit 再读流：stdout/stderr 都重定向了，若在读流之前不等待，
        // 子进程一旦写满 pipe buffer 就会卡住，而父进程还在等它退出 —— 死锁。
        //
        // 也要设超时：security / update-ca-certificates 都可能弹交互框等输入，
        // 无头 agent 里没人能应答，裸 WaitForExit() 会把整个 start 命令挂死。
        if (!process.WaitForExit(20000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }

            throw new TimeoutException($"{what} timed out after 20s: {args[0]} {string.Join(' ', args.Skip(1))}");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
            AgentLog.Info($"{what} output: {(stdout + stderr).Trim()}");

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{what} failed (exit {process.ExitCode}): {stderr.Trim()}");
    }
}
