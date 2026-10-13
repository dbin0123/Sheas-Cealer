using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Cealing_Agent;

internal static class TrustStore
{
    internal const string RootCertSubjectName = "CN=Cealing Cert Root";
    internal const string ChildCertSubjectName = "CN=Cealing Cert Child";

    private const string RootCertCommonName = "Cealing Cert Root";

    private const string MacSystemKeychain = "/Library/Keychains/System.keychain";
    private const string LinuxCertDir = "/usr/local/share/ca-certificates";
    private const string LinuxCertFile = "cealing-root.crt";

    internal static string LinuxRootCertPath => Path.Combine(LinuxCertDir, LinuxCertFile);

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

    /// <summary>
    /// 卸载专用：按「我们装上去的东西长什么样」来删，而不是按「本次进程记住了哪个缩印」。
    /// 上一任 agent 被 SIGKILL、崩溃或断电带走时，它装的系统根证书不会被任何后继进程记住，
    /// 只认缩印的 Uninstall() 对那种残留完全无能为力 —— 机器上就永久留了一张可信 MITM 根。
    /// </summary>
    internal static void Purge(string platform)
    {
        try
        {
            switch (platform)
            {
                case "linux":
                    PurgeLinux();
                    break;
                case "windows":
                    PurgeWindowsStore();
                    break;
                case "macos":
                    // 删系统钥匙串里的证书不弹授权框（只有 add-trusted-cert 会），
                    // 卸载路径又是前台一次性进程，所以这里可以比常驻 agent 多做一步。
                    PurgeMacSystemKeychain();
                    break;
                default:
                    throw new PlatformNotSupportedException($"unsupported platform: {platform}");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"purge root cert skipped: {ex.Message}");
        }
    }

    private static void PurgeLinux()
    {
        // update-ca-certificates --fresh 要重建整个 /etc/ssl/certs（数秒）。
        // 我们的 crt 早就不在时不必白跑一次，否则每次卸载都白等且日志刷屏。
        if (!File.Exists(LinuxRootCertPath))
        {
            AgentLog.Info($"no {LinuxRootCertPath} to purge");
            return;
        }

        Run(BuildUninstallArgs(string.Empty, "linux"), "purge root cert");
    }

    // Windows 的机器级 Root 存储里按主题找，找到几个删几个：
    // 反复「装-崩-装」可以留下多张同主题不同密钥的根证书，只删一张等于没删干净。
    private static void PurgeWindowsStore()
    {
        if (!OperatingSystem.IsWindows())
            return;

        List<string> thumbprints = [];

        using (X509Store store = new(StoreName.Root, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);

            foreach (X509Certificate2 certificate in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, RootCertSubjectName, false))
            {
                using (certificate)
                {
                    thumbprints.Add(certificate.Thumbprint);
                    store.Remove(certificate);
                }
            }
        }

        AgentLog.Info($"purged {thumbprints.Count} root cert(s) from the windows certificate store: {string.Join(", ", thumbprints)}");
    }

    // 必须按 SHA-1 逐个删：`security delete-certificate -c "Cealing Cert Root"` 一次只带走一张，
    // 而反复「装-崩-装」会在系统钥匙串里留下多张同名根（GUI 侧实测删完仍剩 6 张）。
    // 系统域残留是机器级信任，比普通用户钥匙串更要紧，不能只删一张就当清干净了。
    private static void PurgeMacSystemKeychain()
    {
        List<string> hashes = [];

        foreach (string line in Capture(["security", "find-certificate", "-a", "-c", RootCertCommonName, "-Z", MacSystemKeychain]).Split('\n'))
        {
            int at = line.IndexOf("SHA-1 hash", StringComparison.Ordinal);

            if (at == -1)
                continue;

            // `security` 的输出是 SHA-1 hash="AB CD EF ..."（带引号和空格），
            // 这里一律去掉两者再收，不依赖它到底带不带引号。
            string hash = line[(line.IndexOf('=', at) + 1)..].Replace(" ", "").Replace("\"", "");

            if (hash.Length == 40)
                hashes.Add(hash);
        }

        foreach (string hash in hashes)
            Run(["security", "delete-certificate", "-Z", hash, MacSystemKeychain], $"purge system root cert {hash}");

        AgentLog.Info($"purged {hashes.Count} root cert(s) from {MacSystemKeychain}");
    }

    // NSS 用户库（Linux 上 Chromium/Edge 唯一认的本地信任库）里的根是 GUI 以普通用户身份装的，
    // 包管理器的卸载脚本和 agent 的常规清理都够不着它 —— 卸载后浏览器会继续信任我们的 MITM 根。
    // agent 以 root 运行，可以替任何用户删掉这一条。certutil 缺失（没装 libnss3-tools）时
    // 什么都不做：当初也装不进去，没有残留可删。
    internal static void PurgeNss(string home)
    {
        string database = Path.Combine(home, ".pki", "nssdb");

        if (!Directory.Exists(database))
            return;

        try
        {
            Capture(["certutil", "-D", "-n", RootCertCommonName, "-d", $"sql:{database}"], $"purge nss root cert for {home}");
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not purge nss root cert for {home}: {ex.Message}");
        }
    }

    // internal：JavaTrust 也要跑外部命令（keytool），必须复用同一个进程执行器，
    // 不能出现第二份「先等退出还是先读流」的实现。
    internal static void Run(IReadOnlyList<string> args, string what)
    {
        (int exitCode, string output) = Capture(args, what);

        if (exitCode != 0)
            throw new InvalidOperationException($"{what} failed (exit {exitCode}): {output.Trim()}");
    }

    private static string Capture(IReadOnlyList<string> args) => Capture(args, $"run {args[0]}").output;

    // 卸载路径里也要跑外部命令（reg.exe），这里开放给 Uninstaller 复用，
    // 免得 agent 里出现第二个进程执行器 —— 超时和「先等退出再读流」那两个坑只有一份。
    internal static (int exitCode, string output) Capture(IReadOnlyList<string> args, string what)
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

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(output))
            AgentLog.Info($"{what} output: {output.Trim()}");

        return (process.ExitCode, output);
    }
}
