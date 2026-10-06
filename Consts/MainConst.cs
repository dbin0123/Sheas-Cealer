using Cealing_Core;
using Microsoft.Win32;
using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Sheas_Cealer_Nix.Consts;

internal abstract partial class MainConst : MainMultilangConst
{
    internal enum SettingsMode
    { BrowserPathMode, UpstreamUrlMode, ExtraArgsMode, NginxEngineMode }

    internal enum NginxEngineMode
    { AutoMode, ExternalMode, BuiltinMode }

    // 判定实现只剩 Cealing_Core.Privilege 一份。以前这里、PrivilegeEscalator、agent
    // 各写一遍，「GUI 以为自己是管理员、agent 其实是普通令牌」的缝隙就漏成了静默失效。
    public static bool IsAdmin => Privilege.IsElevated;

    // 托盘菜单项文案同时是三处的分发 key：macOS 的 StatusBarHelper 回传
    // "click:<item>"、Windows/Linux 的原生托盘 NativeMenuItem.Header、以及
    // MainWin.OnStatusBarMenuClicked 的 switch。写成常量让编译器代管一致性，
    // 否则手抖一个字就是「点了菜单项什么也没发生」。
    internal const string TrayMenuShowWindow = "显示窗口";
    internal const string TrayMenuStartBrowser = "启动伪造";
    internal const string TrayMenuStartGlobal = "启用全局伪造";
    internal const string TrayMenuStopGlobal = "停止全局伪造";
    internal const string TrayMenuUpdateUpstream = "更新上游规则";
    internal const string TrayMenuQuit = "退出";

    internal static string EdgeBrowserRegistryPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";
    internal static string ChromeBrowserRegistryPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
    internal static string BraveBrowserRegistryPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe";

    // macOS 的浏览器可执行文件在 .app 包里
    internal static string[] MacBrowserPaths =>
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Microsoft Edge Beta.app/Contents/MacOS/Microsoft Edge Beta",
        "/Applications/Microsoft Edge Dev.app/Contents/MacOS/Microsoft Edge Dev",
        "/Applications/Google Chrome Beta.app/Contents/MacOS/Google Chrome Beta",
        "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary",
        "/Applications/Brave Browser Beta.app/Contents/MacOS/Brave Browser Beta"
    ];

    internal static string DefaultUpstreamUrl => "https://gitlab.com/SpaceTimee/Cealing-Host/raw/main/Cealing-Host.json";

    // 二进制目录（.app/Contents/MacOS 或 Linux 可执行文件所在目录），只读
    internal static string AppDir => AppDomain.CurrentDomain.SetupInformation.ApplicationBase!;

    // 用户可写的数据目录。升级会整包替换 bundle，旧版把配置/证书/日志写在 AppDir 里，
    // 每次升级都会丢，所以统一挪到这里，见 DataMigration。
    internal static string DataDir => AppPaths.DataDir;

    // 提权进程眼里的「应用目录」：bundle 挂在 root 执行不了的 FUSE 上时（AppImage），
    // 这里换成 BinaryStager 复制出来的真实目录。传给 agent 的 --app-dir、写进配置的引擎
    // 二进制路径、以及复用 agent 时的身份比对都必须用这一个值，否则 agent 会被判成
    // 「应用目录不一致」，每次点全局伪造都白杀白重起一轮。
    internal static string AgentAppDir => BinaryStager.StagedDir ?? AppDir;

    internal static string CealHostPath => Path.Combine(DataDir, "Cealing-Host-*.json");
    internal static string LocalHostPath => Path.Combine(DataDir, "Cealing-Host-L.json");
    internal static string UpstreamHostPath => Path.Combine(DataDir, "Cealing-Host-U.json");

    internal static string HostsConfPath => OperatingSystem.IsWindows() ? Path.Combine(Registry.LocalMachine.OpenSubKey(@"\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\DataBasePath")?.GetValue("DataBasePath", null)?.ToString() ?? @"C:\Windows\System32\drivers\etc", "hosts") : "/etc/hosts";
    internal static string HostsConfStartMarker => $"# Cealing Nginx Start{Environment.NewLine}";
    internal static string HostsConfEndMarker => "# Cealing Nginx End";

    internal static string ConginxPath => BinPath("Cealing-Conginx");
    internal static string NginxPath => BinPath("Cealing-Nginx");
    internal static string NginxConfPath => Path.Combine(DataDir, "nginx.conf");
    internal static string NginxLogsPath => Path.Combine(DataDir, "logs");
    internal static string NginxErrorLogsPath => Path.Combine(NginxLogsPath, "error.log");
    internal static string NginxTempPath => Path.Combine(DataDir, "temp");
    internal static string NginxCertPath => Path.Combine(DataDir, "Cealing-Cert.pem");
    internal static string NginxKeyPath => Path.Combine(DataDir, "Cealing-Key.pem");
    internal static string NginxRootCertSubjectName => "CN=Cealing Cert Root";
    internal static string NginxChildCertSubjectName => "CN=Cealing Cert Child";

    internal static string ComihomoPath => BinPath("Cealing-Comihomo");
    internal static string MihomoPath => BinPath("Cealing-Mihomo");
    internal static string MihomoConfPath => Path.Combine(DataDir, "config.yaml");
    internal static string[] MihomoNameServers => ["https://ns.net.kg/dns-query", "https://dnschina1.soraharu.com/dns-query", "https://0ms.dev/dns-query"];

    // macOS/Linux 的 root agent（替代原来在 GUI 进程内做提权动作）
    internal static string AgentBinaryPath => BinPath("Cealing-Agent");
    internal static string ProxyConfigPath => Path.Combine(DataDir, "cealing-proxy.json");
    internal static string AgentLogPath => Path.Combine(DataDir, "cealing-agent.log");
    internal static string GuiErrorLogPath => Path.Combine(DataDir, "sheas-cealer-error.log");

    // agent 生成的根证书（写在 dataDir），GUI 侧用它做 macOS 用户域信任
    internal static string AgentRootCertPath => Path.Combine(DataDir, "Cealing-Root.pem");

    // 提权失败时由 ProxyController.EnsureAgentAsync 写入，Task 10 弹窗时读它。
    internal static string AgentLaunchError { get; set; } = string.Empty;

    // 只有 Windows 需要 .exe 后缀；类 Unix 平台的可执行文件不带扩展名，
    // 且名字会直接成为进程名，所以 Process.GetProcessesByName 才能对上。
    // 这些二进制全都由提权进程（agent，或 agent 拉起的 nginx/mihomo）来执行，
    // 所以路径挂在 AgentAppDir 而不是 AppDir 下。
    private static string BinPath(string name) => Path.Combine(
        AgentAppDir,
        OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    internal static string NotifyIconText => "Sheas Cealer Nix";

    [GeneratedRegex("^Cealing-Host-")]
    internal static partial Regex CealHostPrefixRegex();

    [GeneratedRegex(@"^(https?:\/\/)?[a-zA-Z0-9](-*[a-zA-Z0-9])*(\.[a-zA-Z0-9](-*[a-zA-Z0-9])*)*(:\d{1,5})?(\/[a-zA-Z0-9.\-_\~\!\$\&\'\(\)\*\+\,\;\=\:\@\%]*)*$")]
    internal static partial Regex UpstreamUrlRegex();

    [GeneratedRegex(@"^(--[a-z](-?[a-z])*(=("".*"")|.*)?( --[a-z](-?[a-z])*(="".*"")?)*)?$")]
    internal static partial Regex ExtraArgsRegex();
}