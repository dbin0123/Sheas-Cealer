using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sheas_Cealer_Nix.Utils;

// macOS 的根证书信任：**系统（admin）域**需要交互授权，从后台 root agent 进程安装会失败
// （SecTrustSettingsSetTrustSettings: The authorization was denied since no user interaction was possible）。
// **用户（user）域**的信任不需要授权，而 GUI 本来就是以该用户身份运行的，所以由 GUI 来做。
// 效果是浏览器和本机其它应用都会信任这张根证书；停止/退出时删除。
internal static class UserTrust
{
    private const string RootCertSubjectName = "Cealing Cert Root";
    private const string SystemKeychain = "/Library/Keychains/System.keychain";

    private static string LoginKeychain => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");

    internal static void Install()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(MainConst.AgentRootCertPath))
            return;

        // 登录域：不需要授权，缺就补上（用于本机其它用户级应用）。
        // 注意不能因为「登录域已信任」就整体提前返回 —— 浏览器（Chromium/Edge）只认
        // 系统域的信任根，只装登录域时浏览器会直接拒 TLS（页面打不开、curl 加 -k 却正常）。
        if (!IsTrustedInLogin())
        {
            RemoveStaleLoginRoots();

            Run("add-trusted-cert", "-r", "trustRoot", "-k", LoginKeychain, MainConst.AgentRootCertPath);
        }

        // 系统域：浏览器真正信任的地方，缺就装（需要一次管理员授权）。
        // 根证书已改为稳定复用，所以正常只会授权一次，不会每次启动都弹。
        if (!IsTrustedInSystem())
            InstallToSystemWithAuthorization();
    }

    /// <summary>
    /// 当前根证书是否在**登录钥匙串**里受信任（无需管理员权限）。
    /// </summary>
    internal static bool IsTrustedInLogin()
    {
        if (!OperatingSystem.IsMacOS())
            return true;

        string? thumbprint = CurrentRootThumbprint();

        if (thumbprint is null)
            return false;

        try
        {
            return Run("find-certificate", "-a", "-c", RootCertSubjectName, "-Z", LoginKeychain)
                .Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前根证书是否已被信任。登录域与系统域任意一个信任即算已信任 ——
    /// 供「是否还需要弹授权框」这类判断使用。注意这**不代表浏览器会信任**：
    /// Chromium/Edge 只认系统域，判断浏览器可用性必须用 IsTrustedInSystem()。
    /// </summary>
    internal static bool IsTrusted()
    {
        if (!OperatingSystem.IsMacOS())
            return true;

        string? thumbprint = CurrentRootThumbprint();

        if (thumbprint is null)
            return false;

        try
        {
            foreach (string keychain in new[] { LoginKeychain, SystemKeychain })
            {
                if (Run("find-certificate", "-a", "-c", RootCertSubjectName, "-Z", keychain)
                    .Contains(thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查系统钥匙串中是否已信任根证书
    /// </summary>
    internal static bool IsTrustedInSystem()
    {
        if (!OperatingSystem.IsMacOS())
            return true;

        try
        {
            // 必须按**指纹**匹配当前这张根，不能只看"有没有同名证书"。
            // 旧实现只查同名是否存在：钥匙串里残留一张别的根时就误判为已信任，
            // 于是当前根永远装不进去，代理出示的叶证书与浏览器信任的根对不上链，
            // TLS/ALPN 协商被拒（ERR_HTTP2_PROTOCOL_ERROR / ERR_CONNECTION_RESET）。
            string thumbprint = CurrentRootThumbprint();

            if (thumbprint is null)
                return false;

            string output = Run("find-certificate", "-a", "-c", RootCertSubjectName, "-Z", SystemKeychain);

            return output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // 当前 Cealing-Root.pem 的 SHA-1 指纹（大写、无冒号），读不到返回 null。
    private static string? CurrentRootThumbprint()
    {
        try
        {
            if (!File.Exists(MainConst.AgentRootCertPath))
                return null;

            using System.Security.Cryptography.X509Certificates.X509Certificate2 root =
                System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(MainConst.AgentRootCertPath);

            return root.Thumbprint;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 使用 osascript 触发系统授权对话框，将证书安装到系统钥匙串
    /// </summary>
    internal static bool InstallToSystemWithAuthorization()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(MainConst.AgentRootCertPath))
            return false;

        // 只看系统域：登录域已信任**不代表**浏览器会信任，这里不能用 IsTrusted() 提前返回。
        if (IsTrustedInSystem())
            return true;

        try
        {
            // 使用 osascript 触发系统授权对话框
            string script = $@"
                tell application ""System Events""
                    set certPath to ""{MainConst.AgentRootCertPath}""
                    set certFile to POSIX file certPath
                end tell
                do shell script ""security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain '{MainConst.AgentRootCertPath}'"" with administrator privileges
            ";

            ProcessStartInfo startInfo = new("osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);

            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60000);

            return process.ExitCode == 0 && IsTrustedInSystem();
        }
        catch
        {
            return false;
        }
    }

    internal static void Remove()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // 必须按 SHA-1 删：`security delete-certificate -c "Cealing Cert Root"` 在存在多张同名证书时删不掉
        // （实测删完仍剩 6 张）。按指纹逐个删才能清干净。
        foreach (string hash in FindRootHashes())
            Run("delete-certificate", "-Z", hash, LoginKeychain);

        // 系统域删除需要管理员权限，Run 不会提权，删不掉是预期的；系统域仅作 best-effort 清理。
        foreach (string hash in FindSystemRootHashes())
            Run("delete-certificate", "-Z", hash, SystemKeychain);
    }

    // 只清登录域的同名旧根。Install() 用它替代原来的 Remove()，避免连带清系统域。
    private static void RemoveStaleLoginRoots()
    {
        string? thumbprint = CurrentRootThumbprint();

        foreach (string hash in FindRootHashes())
        {
            if (hash.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                continue;

            Run("delete-certificate", "-Z", hash, LoginKeychain);
        }
    }

    private static IEnumerable<string> FindRootHashes() =>
        Run("find-certificate", "-a", "-c", RootCertSubjectName, "-Z", LoginKeychain)
            .Split('\n')
            .Where(line => line.Contains("SHA-1 hash"))
            .Select(line => line[(line.LastIndexOf(' ') + 1)..].Trim())
            .Where(hash => hash.Length == 40);

    private static IEnumerable<string> FindSystemRootHashes() =>
        Run("find-certificate", "-a", "-c", RootCertSubjectName, "-Z", SystemKeychain)
            .Split('\n')
            .Where(line => line.Contains("SHA-1 hash"))
            .Select(line => line[(line.LastIndexOf(' ') + 1)..].Trim())
            .Where(hash => hash.Length == 40);

    private static string Run(params string[] args)
    {
        try
        {
            ProcessStartInfo startInfo = new("security") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using Process? process = Process.Start(startInfo);

            if (process is null)
                return string.Empty;

            string output = process.StandardOutput.ReadToEnd();

            process.WaitForExit(15000);

            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
