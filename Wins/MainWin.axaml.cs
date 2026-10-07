using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cealing_Core;
using Cealing_Core.Protocol;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using NginxConfigParser;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Preses;
using Sheas_Cealer_Nix.Proces;
using Sheas_Cealer_Nix.Utils;
using AppSettings = Sheas_Cealer_Nix.Props.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using File = System.IO.File;

namespace Sheas_Cealer_Nix.Wins;

public partial class MainWin : Window
{
    private readonly MainPres MainPres;
    private readonly HttpClient MainClient = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
    private DispatcherTimer? HoldButtonTimer;
    private readonly DispatcherTimer ProxyTimer = new() { Interval = TimeSpan.FromSeconds(0.1) };
    // NotifyFilter 必须带上 FileName：只写 LastWrite 的话，新建文件（比如第一次「更新上游规则」
    // 才创建出的 Cealing-Host-U.json）不会触发 Created 事件，规则就永远是 0 条。
    private readonly FileSystemWatcher CealHostWatcher = new(Path.GetDirectoryName(MainConst.CealHostPath)!, Path.GetFileName(MainConst.CealHostPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
    private readonly FileSystemWatcher NginxConfWatcher = new(Path.GetDirectoryName(MainConst.NginxConfPath)!, Path.GetFileName(MainConst.NginxConfPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite };
    private readonly FileSystemWatcher MihomoConfWatcher = new(Path.GetDirectoryName(MainConst.MihomoConfPath)!, Path.GetFileName(MainConst.MihomoConfPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite };
    private ProxyController? ProxyController;

    private readonly SortedDictionary<string, List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>?> CealHostRulesDict = [];
    private string CealArgs = string.Empty;
    private NginxConfig? NginxConfs;
    private string? ExtraNginxConfs;
    private string? ComihomoConfs;
    private string? HostsComihomoConfs;
    private string? MihomoConfs;
    private string? ExtraMihomoConfs;

    private int NginxHttpPort = 80;
    private int NginxHttpsPort = 443;
    private int MihomoMixedPort = 7880;

    private int GameClickTime = 0;
    private int GameFlashInterval = 1000;

    // 「点 X = 隐藏到托盘」是否成立，取决于真的有个能点回来的托盘入口。
    private bool _canHideToTray;

    // 退出时等 agent 应答 Shutdown 的上限。协议层自己的超时是 30s，
    // 直接 await 会让「退出」看起来卡死；超过这个上限就放弃等待、照常退出。
    private static readonly TimeSpan ExitHandshakeWait = TimeSpan.FromSeconds(8);

    public MainWin()
    {
        DataContext = MainPres = new();

        InitializeComponent();
    }
    //private void MainWin_SourceInitialized(object sender, EventArgs e)
    //{
    //    IconRemover.RemoveIcon(this);
    //    BorderThemeSetter.SetBorderTheme(this, MainPres.IsLightTheme);
    //}
    private async void MainWin_Loaded(object sender, RoutedEventArgs e)
    {
        // 启动菜单栏图标助手（只有 macOS 有这个东西）
        if (OperatingSystem.IsMacOS())
        {
            StatusBarHelper.MenuClicked += OnStatusBarMenuClicked;
            StatusBarHelper.Start(this);
        }

        // 见 Utils/X11CloseProtocol：不摘掉帧同步声明的话，Cinnamon 点 X 会直接杀进程，
        // 下面的 Closing→Hide 根本没机会跑。
        X11CloseProtocol.StripFrameSync(this);

        // macOS 的托盘由外部 StatusBarHelper 二进制提供（csproj 只在 OSX 上编译它），
        // 其余平台只能用 Avalonia 原生托盘。两边都没有的话，点 X 隐藏就等于
        // 造出一个看不见也叫不回来的残留进程 —— 所以这里把「能不能隐藏」记下来交给 Closing 判断。
        _canHideToTray = StatusBarHelper.IsRunning
            || Application.Current is { } app && NativeTray.Attach(app, OnStatusBarMenuClicked);

        // macOS 的 Cmd+Q（以及任何 desktop.Shutdown() 调用）只会走 lifetime 的退出流程，
        // 那条流程里没人收 agent。所以先取消它，再把请求转进 QuitAsync 这个统一漏斗：
        // 只 cancel 不接管的话，Cmd+Q 就成了「按了没反应」——用户只能从活动监视器强杀，
        // 那时 root agent 只能靠主人租约断连才自杀，引擎清理全看运气。
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownRequested += (_, e) =>
            {
                e.Cancel = true;

                _ = QuitAsync();
            };

        BrowserButton.AddHandler(Button.PointerPressedEvent, LaunchButton_PointerPressed, handledEventsToo: true);
        NginxButton.AddHandler(Button.PointerPressedEvent, LaunchButton_PointerPressed, handledEventsToo: true);
        MihomoButton.AddHandler(Button.PointerPressedEvent, LaunchButton_PointerPressed, handledEventsToo: true);

        ProxyTimer.Tick += ProxyTimer_Tick;
        CealHostWatcher.Changed += CealHostWatcher_Changed;
        CealHostWatcher.Created += CealHostWatcher_Changed;
        NginxConfWatcher.Changed += NginxConfWatcher_Changed;
        MihomoConfWatcher.Changed += MihomoConfWatcher_Changed;

        ProxyTimer.Start();

        foreach (string cealHostPath in Directory.GetFiles(CealHostWatcher.Path, CealHostWatcher.Filter))
            CealHostWatcher_Changed(null!, new(new(), Path.GetDirectoryName(cealHostPath)!, Path.GetFileName(cealHostPath)));

        ProxyController = new ProxyController(
            () => CealHostRulesDict,
            () => MainPres.IsFlashing,
            () => MainPres.IsBuiltinProxyEngine ? ProxyEngineKind.Builtin : ProxyEngineKind.External,
            () => MainPres.ResolvedNginxPath);

        await Task.Run(async () =>
        {
            // 不在这里无条件 EnsureAgentAsync —— 那会让每次启动都弹系统授权框。
            // 正常启动时没有 agent（退出时会 shutdown），先只探测是否恰好已有存活的 agent。
            if (ProxyController.Client.IsAlive && await ProxyController.TryGetStatusAsync() is not null)
                MainPres.IsAgentReady = true;

            if (MainPres.IsAgentReady)
                await NginxCleaner.Clean();
        });

        // 只给用过全局伪造的用户弹授权询问，纯浏览器用户不该被无故打扰。
        if (!MainPres.IsAgentReady && AppSettings.Default.PromptAgentOnStartup)
        {
            ButtonResult answer = await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._AgentLaunchPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this);

            if (answer == ButtonResult.Yes)
                MainPres.IsAgentReady = await ProxyController.EnsureAgentAsync();
        }

        AppSettings.Default.PromptAgentOnStartup = MainPres.IsAgentReady;
        AppSettings.Default.Save();

        if (Array.Exists(Environment.GetCommandLineArgs(), arg => arg.Equals("-s", StringComparison.OrdinalIgnoreCase)))
            LaunchButton_Click(null, (RoutedEventArgs)RoutedEventArgs.Empty);

        UpdateUpstreamHostButton_Click(null, null!);
    }
    private void MainWin_Closing(object sender, WindowClosingEventArgs e)
    {
        // 两条路都不允许「窗口没了、进程静悄悄活着」：
        // 有托盘 → 隐藏到托盘（macOS 一直如此，现在 Windows/Linux 也补上了原生托盘）；
        // 没托盘 → 关闭就是退出，而且必须走 QuitAsync 这个统一漏斗，
        // 不能直接把退出交给 lifetime（那样 agent 又不会被带走）。
        e.Cancel = true;

        if (_canHideToTray)
            Hide();
        else
            _ = QuitAsync();
    }

    // 退出的唯一漏斗：托盘「退出」、Ctrl+W、以及没有托盘时的点 X 都走这里。
    private async Task QuitAsync()
    {
        StatusBarHelper.Quit();

        if (ProxyController is not null)
        {
            try
            {
                // 全局伪造开着时必须先显式停掉再退出：StopAsync 内部会先 UserTrust.Remove()
                // 再让 agent 停引擎、还原 hosts、拆系统根证书，和正常点「停止全局伪造」一致。
                // 这样即使后面的 Shutdown 应答收不到，退出时伪造也已经关掉了。
                if (MainPres.IsProxyRunning)
                {
                    Task<AgentResponse> stop = ProxyController.StopAsync();

                    await Task.WhenAny(AwaitQuietly(stop), Task.Delay(ExitHandshakeWait));
                }

                // agent 是常驻特权进程，退出时必须用 Shutdown 把它一起带走：
                // 它会停引擎、还原 hosts、拆掉根证书。少了这一步，
                // 每次退出都留下一个管理员权限的 Cealing-Agent 和被改过的 hosts。
                //
                // 不能无限等：agent 卡住时退出不能被它拖住（协议层超时是 30s，太久了）。
                Task<AgentResponse> shutdown = ProxyController.ShutdownAsync();

                await Task.WhenAny(AwaitQuietly(shutdown), Task.Delay(ExitHandshakeWait));
            }
            catch
            {
                // 提权后的 agent 也许已经不等了 / socket 已断：退出优先，不能反过来被清理阻塞
            }
        }

        Environment.Exit(0);
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private void OnStatusBarMenuClicked(string item)
    {
        if (item == MainConst.TrayMenuQuit)
        {
            // 以前这里是 StatusBarHelper.Quit() + Environment.Exit(0)：
            // 只收了托盘，没收 agent，于是每次退出都留下一个管理员权限的残留进程。
            _ = QuitAsync();

            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            switch (item)
            {
                case MainConst.TrayMenuShowWindow:
                    Show();
                    Activate();
                    break;
                case MainConst.TrayMenuStartBrowser:
                    Show();
                    Activate();
                    BrowserButtonHoldTimer_Tick(true, EventArgs.Empty);
                    break;
                case MainConst.TrayMenuStartGlobal:
                    if (!MainPres.IsProxyRunning && !MainPres.IsCoproxyIniting && !MainPres.IsNginxIniting)
                    {
                        Show();
                        Activate();
                        NginxButtonHoldTimer_Tick(null, EventArgs.Empty);
                    }
                    break;
                case MainConst.TrayMenuStopGlobal:
                    if (MainPres.IsProxyRunning)
                    {
                        Show();
                        Activate();
                        NginxButtonHoldTimer_Tick(null, EventArgs.Empty);
                    }
                    break;
                case MainConst.TrayMenuUpdateUpstream:
                    Show();
                    Activate();
                    UpdateUpstreamHostButton_Click(this, null!);
                    break;
            }
        });
    }

    private void ExitApplication() => _ = QuitAsync();

    private void MainWin_DragEnter(object sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.GetFiles() != null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private void MainWin_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() != null)
            MainPres.BrowserPath = e.Data.GetFiles()!.FirstOrDefault()!.Path.LocalPath;
    }

    private void SettingsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        switch (MainPres.SettingsMode)
        {
            case MainConst.SettingsMode.BrowserPathMode:
                MainPres.BrowserPath = SettingsBox.Text ?? string.Empty;
                return;
            case MainConst.SettingsMode.UpstreamUrlMode:
                MainPres.UpstreamUrl = SettingsBox.Text ?? string.Empty;
                return;
            case MainConst.SettingsMode.ExtraArgsMode:
                MainPres.ExtraArgs = SettingsBox.Text ?? string.Empty;
                return;
            case MainConst.SettingsMode.NginxEngineMode:
                return;
        }
    }
    private void SettingsModeButton_Click(object sender, RoutedEventArgs e)
    {
        MainPres.SettingsMode = MainPres.SettingsMode switch
        {
            MainConst.SettingsMode.BrowserPathMode => MainConst.SettingsMode.UpstreamUrlMode,
            MainConst.SettingsMode.UpstreamUrlMode => MainConst.SettingsMode.ExtraArgsMode,
            MainConst.SettingsMode.ExtraArgsMode => MainConst.SettingsMode.NginxEngineMode,
            MainConst.SettingsMode.NginxEngineMode => MainConst.SettingsMode.BrowserPathMode,
            _ => throw new UnreachableException()
        };
    }
    private async void SettingsFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (MainPres.SettingsMode)
        {
            case MainConst.SettingsMode.BrowserPathMode:
                IReadOnlyList<IStorageFile> browserFiles = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() { Title = MainConst._BrowserPathDialogFilterFileType, AllowMultiple = false });  //Todo: Filter

                if (browserFiles.Count > 0)
                    MainPres.BrowserPath = browserFiles[0].Path.LocalPath;

                return;
            case MainConst.SettingsMode.UpstreamUrlMode:
                MainPres.UpstreamUrl = MainConst.DefaultUpstreamUrl;
                return;
            case MainConst.SettingsMode.ExtraArgsMode:
                MainPres.ExtraArgs = string.Empty;
                return;
            case MainConst.SettingsMode.NginxEngineMode:
                if (MainPres.IsNginxRunning || MainPres.IsConginxRunning || MainPres.IsNginxIniting || MainPres.IsCoproxyIniting)
                {
                    await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._NginxEngineRunningPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);
                    return;
                }

                MainPres.NginxEngineMode = MainPres.NginxEngineMode switch
                {
                    MainConst.NginxEngineMode.AutoMode => MainConst.NginxEngineMode.ExternalMode,
                    MainConst.NginxEngineMode.ExternalMode => MainConst.NginxEngineMode.BuiltinMode,
                    _ => MainConst.NginxEngineMode.AutoMode
                };
                return;
        }
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (HoldButtonTimer is { IsEnabled: false })
            return;

        Button? senderButton = sender as Button;

        if (senderButton == NginxButton)
            NginxButtonHoldTimer_Tick(null, EventArgs.Empty);
        else if (senderButton == MihomoButton)
            MihomoButtonHoldTimer_Tick(null, EventArgs.Empty);
        else
            BrowserButtonHoldTimer_Tick(sender == null, EventArgs.Empty);
    }
    private void LaunchButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Button senderButton = (Button)sender!;

        HoldButtonTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        HoldButtonTimer.Tick += senderButton == NginxButton ? NginxButtonHoldTimer_Tick : senderButton == MihomoButton ? MihomoButtonHoldTimer_Tick : BrowserButtonHoldTimer_Tick;
        HoldButtonTimer.Start();
    }
    private async void BrowserButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();

        if ((CealHostRulesDict.ContainsValue(null!) && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._CealHostErrorPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (sender is not true && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._KillBrowserProcessPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes))
            return;

        foreach (Process browserProcess in Process.GetProcessesByName(PlatformHelper.ProcName(MainPres.BrowserPath)))
        {
            browserProcess.Kill();
            await browserProcess.WaitForExitAsync();
        }

        await Task.Run(() =>
        {
            new BrowserProc(MainPres.BrowserPath, sender is bool).Run(Path.GetDirectoryName(MainPres.BrowserPath)!, $"{CealArgs} {MainPres.ExtraArgs.Trim()} {(MainConst.IsAdmin ? "--no-sandbox" : string.Empty)}");
        });
    }
    private async void NginxButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();

        if (MainPres.IsProxyRunning)
        {
            AgentResponse stopResponse = await ProxyController!.StopAsync();

            if (!stopResponse.Ok)
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, stopResponse.Error ?? MainConst._NginxEngineMissingPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

            MainPres.IsProxyRunning = false;
            MainPres.ProxyEngineName = "none";
            MainPres.IsConginxRunning = false;
            MainPres.IsNginxRunning = false;
            MainPres.IsMihomoRunning = false;

            UpdateProxyStatusText();

            NginxHttpPort = 80;
            NginxHttpsPort = 443;

            return;
        }

        if (ProxyController is null || !await ProxyController.EnsureAgentAsync())
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty,
                MainConst.AgentLaunchError.Length > 0 ? MainConst.AgentLaunchError : MainConst._AgentUnavailablePrompt,
                ButtonEnum.Ok).ShowWindowDialogAsync(this);

            return;
        }

        // 记住 agent 已就绪，否则 MainWin_Closing 不会 shutdown，agent 会泄漏成 root 僵尸进程。
        MainPres.IsAgentReady = true;

        // agent 就绪后重新生成 mihomo 配置（之前因 IsAgentReady=false 被跳过）
        MihomoConfWatcher_Changed(null!, null!);

        // coproxy（mihomo TUN + nginx）依赖外部 mihomo 二进制；没装 mihomo 时自动下载。
        // coproxy 支持通配符 DNS 匹配（hosts 文件不支持），是访问通配符域名的正确方式。
        if (!MainPres.IsMihomoExist && !MainPres.IsComihomoExist)
        {
            // 尝试自动下载 mihomo
            string? mihomoPath = await MihomoDownloader.EnsureAsync(MainConst.MihomoPath);
            if (mihomoPath is not null)
            {
                MainPres.IsMihomoExist = true;
            }
        }

        bool mihomoAvailable = MainPres.IsMihomoExist || MainPres.IsComihomoExist;
        bool isCoproxy = sender == null && mihomoAvailable;
        bool writeHosts = !isCoproxy;
        bool isBuiltin = MainPres.IsBuiltinProxyEngine;
        string? externalNginxPath = isBuiltin ? null : NginxFinder.Find(isCoproxy);

        if (!isBuiltin && externalNginxPath is null)
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._NginxEngineMissingPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);
            return;
        }

        // 没有任何可用规则时直接提示，避免「代理起来了但 rules=0、什么都不伪造」的迷惑状态。
        if (!CealHostRulesDict.Values.Any(rules => rules is { Count: > 0 }))
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._NoCealHostRulesPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

            return;
        }

        if ((CealHostRulesDict.ContainsValue(null!) && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._CealHostErrorPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (NginxHttpsPort != 443 && await MessageBoxManager.GetMessageBoxStandard(string.Empty, string.Format(MainConst._NginxHttpsPortOccupiedPrompt, NginxHttpsPort), ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (NginxHttpPort != 80 && await MessageBoxManager.GetMessageBoxStandard(string.Empty, string.Format(MainConst._NginxHttpPortOccupiedPrompt, NginxHttpPort), ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (writeHosts && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchHostsNginxPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchProxyPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (MainPres.IsFlashing && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchNginxFlashingPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes))
            return;

        if (!File.Exists(MainConst.NginxConfPath))
            await File.Create(MainConst.NginxConfPath).DisposeAsync();
        if (!Directory.Exists(MainConst.NginxLogsPath))
            Directory.CreateDirectory(MainConst.NginxLogsPath);
        if (!Directory.Exists(MainConst.NginxTempPath))
            Directory.CreateDirectory(MainConst.NginxTempPath);

        // 只有用应用自带的 nginx 时才需要把主程序挪成 coproxy 的文件名；
        // 走系统 nginx 时应用目录里可能根本没有这个文件，搬运会直接抛异常。
        if (isCoproxy && !isBuiltin && NginxFinder.IsBundled(externalNginxPath!, true) && !File.Exists(MainConst.ConginxPath))
            File.Move(MainConst.NginxPath, MainConst.ConginxPath);

        if (isCoproxy)
            MainPres.IsCoproxyIniting = true;
        else
            MainPres.IsNginxIniting = true;

        UpdateProxyStatusText();

        NginxConfWatcher.EnableRaisingEvents = false;

        try
        {
            ProxyConfig config = ProxyController.BuildConfig(
                coproxy: isCoproxy,
                writeHosts: writeHosts,
                httpPort: NginxHttpPort,
                httpsPort: NginxHttpsPort,
                mixedPort: MihomoMixedPort,
                nginxConfText: NginxConfs?.ToString(),
                mihomoConfText: isCoproxy ? ComihomoConfs : null);

            config.NginxBinaryPath = externalNginxPath;
            config.MihomoBinaryPath = isCoproxy ? MainConst.ComihomoPath : MainConst.MihomoPath;
            config.MihomoConfText = isCoproxy ? ComihomoConfs : MihomoConfs;

            AgentResponse response = await ProxyController.WriteAndStartAsync(config);

            if (!response.Ok)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, response.Error ?? MainConst._LaunchNginxErrorPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

                return;
            }

            // 轮询等待代理真正就绪（mihomo/nginx 启动、TUN 配置完成）
            bool ready = await WaitForProxyReadyAsync(TimeSpan.FromSeconds(30));

            if (!ready)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, "代理启动超时，请检查日志。", ButtonEnum.Ok).ShowWindowDialogAsync(this);
                return;
            }

            MainPres.IsProxyRunning = true;
            MainPres.ProxyEngineName = isCoproxy ? "mihomo" : config.Engine.ToString().ToLowerInvariant();

            if (isCoproxy)
            {
                MainPres.IsConginxRunning = true;
                MainPres.IsMihomoRunning = true;
            }
            else
            {
                MainPres.IsNginxRunning = true;
            }

            // 代理启动后再检查证书信任状态
            _ = Task.Run(async () =>
            {
                // Linux：浏览器读的是用户的 NSS 库，写入失败（多半是没装 libnss3-tools）
                // 必须说出来，否则用户只看到 ERR_CERT_AUTHORITY_INVALID，无从下手。
                if (OperatingSystem.IsLinux())
                {
                    if (!UserTrust.IsTrustedInNss())
                        await MessageBoxManager.GetMessageBoxStandard(string.Empty,
                            "无法把代理根证书写入浏览器的 NSS 信任库（~/.pki/nssdb），浏览器会继续报证书错误。请安装 libnss3-tools 后重试。",
                            ButtonEnum.Ok).ShowWindowDialogAsync(this);

                    return;
                }

                // 必须按系统域判断：Chromium/Edge 只认系统钥匙串的信任根。
                // 用 IsTrusted()（登录或系统任一）会在只有登录域有根时误判为已信任，
                // 从此不再弹授权框，浏览器就永久拒 TLS。
                if (!UserTrust.IsTrustedInSystem())
                {
                    await MessageBoxManager.GetMessageBoxStandard(string.Empty,
                        "需要管理员权限信任代理证书，即将弹出系统授权对话框。",
                        ButtonEnum.Ok).ShowWindowDialogAsync(this);

                    await Task.Run(() => UserTrust.InstallToSystemWithAuthorization());
                }
            });
        }
        finally
        {
            if (!isCoproxy)
                MainPres.IsNginxIniting = false;
            else
                MainPres.IsCoproxyIniting = false;

            if (!isBuiltin)
                await File.WriteAllTextAsync(MainConst.NginxConfPath, ExtraNginxConfs ?? string.Empty);

            NginxConfWatcher.EnableRaisingEvents = true;

            UpdateProxyStatusText();
        }
    }
    private async void MihomoButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();

        if (MainPres.IsMihomoRunning || MainPres.IsComihomoRunning)
        {
            if (ProxyController is null || !MainPres.IsAgentReady)
                return;

            await ProxyController.StopAsync();

            MainPres.IsMihomoRunning = MainPres.IsComihomoRunning = false;

            if (MainPres.IsConginxRunning)
            {
                MainPres.IsConginxRunning = false;

                if (File.Exists(MainConst.ConginxPath))
                    File.Move(MainConst.ConginxPath, MainConst.NginxPath);
            }

            MainPres.IsProxyRunning = false;
            MainPres.ProxyEngineName = "none";

            UpdateProxyStatusText();

            return;
        }

        if (ProxyController is null || !await ProxyController.EnsureAgentAsync())
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._AgentUnavailablePrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

            return;
        }

        MainPres.IsAgentReady = true;

        if (!MainPres.IsMihomoExist && !MainPres.IsComihomoExist)
        {
            // 尝试自动下载 mihomo
            string? mihomoPath = await MihomoDownloader.EnsureAsync(MainConst.MihomoPath);
            if (mihomoPath is not null)
                MainPres.IsMihomoExist = true;
            else
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._MihomoMissingPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);
                return;
            }
        }

        if (!MainPres.IsConginxRunning && !MainPres.IsCoproxyStopping && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchProxyPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes)
            return;

        if (MainPres.IsMihomoIniting || MainPres.IsComihomoIniting)
            return;

        MainPres.IsMihomoIniting = true;
        UpdateProxyStatusText();
        MihomoConfWatcher.EnableRaisingEvents = false;

        try
        {
            ProxyConfig config = ProxyController.BuildConfig(
                coproxy: MainPres.IsConginxRunning,
                writeHosts: false,
                httpPort: NginxHttpPort,
                httpsPort: NginxHttpsPort,
                mixedPort: MihomoMixedPort,
                nginxConfText: null,
                mihomoConfText: MihomoConfs);

            config.MihomoBinaryPath = MainPres.IsComihomoExist ? MainConst.ComihomoPath : MainConst.MihomoPath;
            config.MihomoConfText = MainPres.IsConginxRunning
                ? MainPres.IsComihomoExist ? HostsComihomoConfs : ComihomoConfs
                : MihomoConfs;

            AgentResponse response = await ProxyController.WriteAndStartAsync(config);

            if (!response.Ok)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, response.Error ?? MainConst._LaunchMihomoErrorMsg, ButtonEnum.Ok).ShowWindowDialogAsync(this);

                return;
            }

            // 轮询等待代理就绪
            bool ready = await WaitForProxyReadyAsync(TimeSpan.FromSeconds(30));
            if (!ready)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, "代理启动超时。", ButtonEnum.Ok).ShowWindowDialogAsync(this);
                return;
            }

            if (MainPres.IsComihomoExist)
                MainPres.IsComihomoRunning = true;
            else
                MainPres.IsMihomoRunning = true;

            MainPres.IsProxyRunning = true;
            MainPres.ProxyEngineName = "mihomo";
            UpdateProxyStatusText();

            MainPres.IsProxyRunning = true;
            MainPres.ProxyEngineName = "mihomo";
        }
        finally
        {
            MainPres.IsMihomoIniting = false;

            if (!MainPres.IsComihomoIniting)
                await File.WriteAllTextAsync(MainConst.MihomoConfPath, ExtraMihomoConfs);

            MihomoConfWatcher.EnableRaisingEvents = true;
        }
    }

    private async void EditHostButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;
        string cealHostPath = senderButton == EditLocalHostButton ? MainConst.LocalHostPath : MainConst.UpstreamHostPath;

        if (!File.Exists(cealHostPath))
            await File.Create(cealHostPath).DisposeAsync();

        try { PlatformHelper.Open(cealHostPath); }
        catch (UnauthorizedAccessException) { PlatformHelper.OpenAsAdmin(cealHostPath); }
    }
    private async void EditConfButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;
        string confPath;

        if (senderButton == EditHostsConfButton)
        {
            confPath = MainConst.HostsConfPath;

            try
            {
                File.SetAttributes(confPath, File.GetAttributes(confPath) & ~FileAttributes.ReadOnly);
            }
            catch (UnauthorizedAccessException)
            {
                // Windows 上非管理员清不动 hosts 的只读位。这里绝不能让它往外抛：
                // 异常从 async void 逃逸会掀掉消息循环，界面上就是「点了没反应、整个卡死」
                // （和 UpdateUpstreamHostButton_Click 修过的同款坑）。
                // 拿不到写权限就提权打开编辑器，否则记事本能看不能存，用户只会以为改了没生效。
                await OpenConfElevatedAsync(confPath);

                return;
            }
        }
        else
        {
            confPath = senderButton == EditNginxConfButton ? MainConst.NginxConfPath : MainConst.MihomoConfPath;

            if (!File.Exists(confPath))
                await File.Create(confPath).DisposeAsync();
        }

        try { PlatformHelper.Open(confPath); }
        catch (Exception ex) { await ShowOpenConfErrorAsync(ex); }
    }

    private async Task OpenConfElevatedAsync(string confPath)
    {
        try { PlatformHelper.OpenAsAdmin(confPath); }
        // UAC 被用户取消是 Win32Exception(1223)，同样不能逃逸成卡死
        catch (Exception ex) { await ShowOpenConfErrorAsync(ex); }
    }

    private Task ShowOpenConfErrorAsync(Exception ex) =>
        MessageBoxManager.GetMessageBoxStandard(string.Empty, $"{MainConst._OpenConfAdminPrompt}{Environment.NewLine}{ex.Message}", ButtonEnum.Ok).ShowWindowDialogAsync(this);
    private async void UpdateUpstreamHostButton_Click(object? sender, RoutedEventArgs e)
    {
        // sender == null 是启动时的静默预检，出错不该打扰用户；
        // 按钮点击（sender != null）必须把错误显示出来，否则异常从 async void 逃逸，
        // 表现就是点一下「更新上游规则」整个界面卡住不动。
        bool interactive = sender is not null;

        try
        {
            if (!File.Exists(MainConst.UpstreamHostPath))
                await File.Create(MainConst.UpstreamHostPath).DisposeAsync();

            string upstreamUpstreamHostUrl = (MainPres.UpstreamUrl.StartsWith("http://") || MainPres.UpstreamUrl.StartsWith("https://") ? string.Empty : "https://") + MainPres.UpstreamUrl;
            string upstreamUpstreamHostString = await Http.GetAsync(upstreamUpstreamHostUrl, MainClient);
            string localUpstreamHostString = await File.ReadAllTextAsync(MainConst.UpstreamHostPath);

            try { upstreamUpstreamHostString = Encoding.UTF8.GetString(Convert.FromBase64String(upstreamUpstreamHostString)); }
            catch { }

            if (sender == null)
            {
                if (localUpstreamHostString != upstreamUpstreamHostString && localUpstreamHostString.ReplaceLineEndings() != upstreamUpstreamHostString.ReplaceLineEndings())
                {
                    await File.WriteAllTextAsync(MainConst.UpstreamHostPath, upstreamUpstreamHostString);
                    MainPres.IsUpstreamHostUtd = true;
                }
            }
            else if (localUpstreamHostString == upstreamUpstreamHostString || localUpstreamHostString.ReplaceLineEndings() == upstreamUpstreamHostString.ReplaceLineEndings())
            {
                MainPres.IsUpstreamHostUtd = true;

                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._UpstreamHostUtdMsg).ShowWindowDialogAsync(this);
            }
            else
            {
                ButtonResult overrideOptionResult = await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._OverrideUpstreamHostPrompt, ButtonEnum.YesNoCancel).ShowWindowDialogAsync(this);

                if (overrideOptionResult == ButtonResult.Yes)
                {
                    await File.WriteAllTextAsync(MainConst.UpstreamHostPath, upstreamUpstreamHostString);

                    MainPres.IsUpstreamHostUtd = true;

                    await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._UpdateUpstreamHostSuccessMsg).ShowWindowDialogAsync(this);
                }
                else if (overrideOptionResult == ButtonResult.No)
                    try { PlatformHelper.Open(upstreamUpstreamHostUrl); }
                    catch (UnauthorizedAccessException) { PlatformHelper.OpenAsAdmin(upstreamUpstreamHostUrl); }
            }
        }
        catch (Exception ex)
        {
            MainPres.IsUpstreamHostUtd = false;

            if (interactive)
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, $"更新上游规则失败：{ex.Message}", ButtonEnum.Ok).ShowWindowDialogAsync(this);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => new SettingsWin().ShowDialog(this);

    // 非模态：看日志的同时还要能回主窗口操作启停
    private LogWin? LogWinInstance;

    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogWinInstance is null)
        {
            LogWinInstance = new LogWin();
            LogWinInstance.Closed += (_, _) => LogWinInstance = null;
            LogWinInstance.Show(this);
        }
        else
            LogWinInstance.Activate();
    }
    private async void NoClickButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameFlashInterval <= 10)
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameReviewEndingMsg).ShowWindowDialogAsync(this);

            return;
        }

        switch (++GameClickTime)
        {
            case 1:
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameClickOnceMsg).ShowWindowDialogAsync(this);
                return;
            case 2:
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameClickTwiceMsg).ShowWindowDialogAsync(this);
                return;
            case 3:
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameClickThreeMsg).ShowWindowDialogAsync(this);
                return;
        }

        if (!MainPres.IsFlashing)
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameStartMsg).ShowWindowDialogAsync(this);
            MainPres.IsFlashing = true;
            NginxConfWatcher_Changed(null!, null!);

            Random random = new();

            while (GameFlashInterval > 10)
            {
                Position = new(random.Next(0, (int)(Screens.Primary!.Bounds.Width - Bounds.Width)), random.Next(0, (int)(Screens.Primary.Bounds.Height - Bounds.Height)));

                //PaletteHelper paletteHelper = new();
                //Theme newTheme = paletteHelper.GetTheme();
                //Color newPrimaryColor = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                //bool isLightTheme = random.Next(2) == 0;

                //newTheme.SetPrimaryColor(newPrimaryColor);
                //newTheme.SetBaseTheme(isLightTheme ? BaseTheme.Light : BaseTheme.Dark);
                //paletteHelper.SetTheme(newTheme);

                //foreach (Window currentWindow in Application.Current.Windows)
                //    BorderThemeSetter.SetBorderTheme(currentWindow, isLightTheme);

                //Style newButtonStyle = new(typeof(Button), Application.Current.Resources[typeof(Button)] as Style);
                //(Color? newForegroundColor, Color newAccentForegroundColor) = ForegroundGenerator.GetForeground(newPrimaryColor.R, newPrimaryColor.G, newPrimaryColor.B);

                //newButtonStyle.Setters.Add(new Setter(ForegroundProperty, newForegroundColor.HasValue ? new SolidColorBrush(newForegroundColor.Value) : new DynamicResourceExtension("MaterialDesignBackground")));
                //Application.Current.Resources[typeof(Button)] = newButtonStyle;

                //MainPres.AccentForegroundColor = newAccentForegroundColor;

                if (GameFlashInterval > 100)
                    GameFlashInterval += random.Next(1, 4);

                await Task.Delay(GameFlashInterval);
            }

            MainPres.IsFlashing = false;
            NginxConfWatcher_Changed(null!, null!);
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._GameEndingMsg).ShowWindowDialogAsync(this);
        }
        else
        {
            switch (GameFlashInterval)
            {
                case > 250:
                    GameFlashInterval -= 150;
                    break;
                case > 100:
                    GameFlashInterval = 100;
                    break;
                case > 10:
                    GameFlashInterval -= 30;
                    break;
            }

            if (GameFlashInterval > 10)
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, $"{MainConst._GameGradeMsg} {GameFlashInterval}").ShowWindowDialogAsync(this);
        }
    }
    private void AboutButton_Click(object sender, RoutedEventArgs e) => new AboutWin().ShowDialog(this);

    private void ProxyTimer_Tick(object? sender, EventArgs e)
    {
        MainPres.RefreshProxyEngine();

        bool isBuiltin = MainPres.IsBuiltinProxyEngine;
        // “存在”要包含系统安装的 nginx，否则只装了 Homebrew nginx 时规则目录不会加载、反代按钮也会一直禁用。
        bool externalNginxFound = NginxFinder.Find() is not null;
        bool externalConginxFound = NginxFinder.Find(coproxy: true) is not null;

        MainPres.IsConginxExist = isBuiltin || externalConginxFound;
        MainPres.IsNginxExist = isBuiltin || externalNginxFound;
        MainPres.IsComihomoExist = File.Exists(MainConst.ComihomoPath);
        MainPres.IsMihomoExist = File.Exists(MainConst.MihomoPath);

        // nginx/mihomo 现在是 agent 的子进程，GUI 拿不到句柄，也不再按进程名探测；
        // 运行状态由 agent 的 start/stop 返回值和 IsProxyRunning / ProxyEngineName 承载。
    }
    private async void CealHostWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        string cealHostName = MainConst.CealHostPrefixRegex().Replace(Path.GetFileNameWithoutExtension(e.Name!), string.Empty);

        try
        {
            // 必须在写入前**不要**重置 dict 条目。
            // 这个回调会被 FileSystemWatcher 在文件正在被替换/拷贝的中间态触发，
            // 那时文件可能是 0 字节或半成品 JSON。先解析成功再覆盖，
            // 否则一次拷贝就能把已加载的上游规则清空 —— 表现是配置只剩本地规则、
            // 证书 SAN 只剩本地域名，浏览器连任何其它域名都拿不到匹配 SAN 的证书，
            // 直接 ERR_CONNECTION_CLOSED。
            await using FileStream cealHostStream = new(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (cealHostStream.Length == 0)
                return;

            JsonDocumentOptions cealHostOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            JsonElement cealHostArray = JsonDocument.Parse(cealHostStream, cealHostOptions).RootElement;

            List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)> cealHostNewRules = [];

            foreach (JsonElement cealHostRule in cealHostArray.EnumerateArray())
            {
                List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs = [];
                // SNI 三态：null = 明确不发送 SNI（proxy_ssl_server_name off）；
                // "" = 无覆盖，用域名本身当 SNI；非空 = 显式覆盖。
                // 旧实现把 "" 变成 "U22" 这类占位符，占位符被当真实 SNI 发给上游，
                // Google 这类必须靠 SNI 选 vhost 的站点会直接断连（ERR_CONNECTION_CLOSED）。
                string? cealHostSni = cealHostRule[1].ValueKind == JsonValueKind.Null ? null :
                    cealHostRule[1].ToString().Trim();
                string cealHostIp = string.IsNullOrWhiteSpace(cealHostRule[2].ToString()) ? "127.0.0.1" : cealHostRule[2].ToString().Trim();

                foreach (JsonElement cealHostDomain in cealHostRule[0].EnumerateArray())
                {
                    string[] cealHostDomainPair = cealHostDomain.ToString().Split('^', 2, StringSplitOptions.TrimEntries);

                    if (string.IsNullOrEmpty(cealHostDomainPair[0].TrimStart('#').TrimStart('$')))
                        continue;

                    cealHostDomainPairs.Add((cealHostDomainPair[0], cealHostDomainPair.Length == 2 ? cealHostDomainPair[1] : string.Empty));
                }

                if (cealHostDomainPairs.Count != 0)
                    cealHostNewRules.Add((cealHostDomainPairs, cealHostSni, cealHostIp));
            }

            CealHostRulesDict[cealHostName] = cealHostNewRules;
        }
        catch (Exception ex)
        {
            // 解析失败**不能**把 dict 条目置 null，否则已加载的规则被永久丢弃
            // （finally 里的重算又只读 dict，等于把清空也生效了）。
            // 必须向上抛：this 是 async void，吞掉会连 finally 一起跑完，
            // 而重抛能让 finally 被跳过，旧值原样保留。
            System.Diagnostics.Debug.WriteLine($"[CealHost] parse failed, keeping previous rules for '{cealHostName}': {ex.Message}");

            throw;
        }
        finally
        {
            string hostRules = string.Empty;
            string hostResolverRules = string.Empty;
            int nullSniNum = 0;

            foreach (KeyValuePair<string, List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>?> cealHostRulesPair in CealHostRulesDict)
                foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp) in cealHostRulesPair.Value ?? [])
                {
                    // Chrome 的两段 MAP 需要一个与真实域名不同的中间名，所以这里仍要造占位符。
                    // （占位符只用于浏览器侧，不参与 TLS SNI。）
                    string cealHostSniWithoutNull = string.IsNullOrEmpty(cealHostSni) ? $"{cealHostRulesPair.Key}{(cealHostRulesPair.Value ?? []).Count + ++nullSniNum}" : cealHostSni;
                    bool isValidCealHostDomainExist = false;

                    foreach ((string cealHostIncludeDomain, string cealHostExcludeDomain) in cealHostDomainPairs)
                    {
                        if (cealHostIncludeDomain.StartsWith('$'))
                            continue;

                        hostRules += $"MAP {cealHostIncludeDomain.TrimStart('#')} {cealHostSniWithoutNull}," + (!string.IsNullOrWhiteSpace(cealHostExcludeDomain) ? $"EXCLUDE {cealHostExcludeDomain}," : string.Empty);
                        isValidCealHostDomainExist = true;
                    }

                    if (isValidCealHostDomainExist)
                        hostResolverRules += $"MAP {cealHostSniWithoutNull} {cealHostIp},";
                }

            CealArgs = @$"--host-rules=""{hostRules.TrimEnd(',')}"" --host-resolver-rules=""{hostResolverRules.TrimEnd(',')}"" --test-type --ignore-certificate-errors";

            NginxConfWatcher_Changed(null!, null!);
            MihomoConfWatcher_Changed(null!, null!);
        }
    }
    private async void NginxConfWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        // 配置是拼给 agent 用的，特权在 agent 身上，不在 GUI 身上。
        // 这里以前按「GUI 是不是管理员」拦 Windows：新模型下 GUI 故意不提权，
        // 于是直接 return，NginxConfs / MihomoConfs 保持空，agent 拿到一份没有规则的
        // 配置 —— 全局伪造点了自然「没有作用」，而且一声不响。
        if (!MainPres.IsAgentReady)
            return;

        if (!MainPres.IsNginxExist && !MainPres.IsConginxExist)
            return;

        if (!File.Exists(MainConst.NginxConfPath))
            await File.Create(MainConst.NginxConfPath).DisposeAsync();
        if (!Directory.Exists(MainConst.NginxLogsPath))
            Directory.CreateDirectory(MainConst.NginxLogsPath);
        if (!Directory.Exists(MainConst.NginxTempPath))
            Directory.CreateDirectory(MainConst.NginxTempPath);

        // 代理正在运行时不要重新探测端口：此时 80/443 正被代理自己占用，
        // 探测会把端口递增成 81/444，停止后也不重置，导致下一次启动用错端口。
        if (!MainPres.IsProxyRunning && !MainPres.IsNginxIniting && !MainPres.IsCoproxyIniting)
        {
            NginxHttpPort = 80;
            NginxHttpsPort = 443;

            foreach (IPEndPoint activeTcpListener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (activeTcpListener.Port == NginxHttpPort)
                    NginxHttpPort++;
                else if (activeTcpListener.Port == NginxHttpsPort)
                    NginxHttpsPort++;
                else if (activeTcpListener.Port > NginxHttpsPort)
                    break;
        }

        await using FileStream nginxConfStream = new(MainConst.NginxConfPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        NginxConfig extraNginxConfig = NginxConfig.Load(ExtraNginxConfs = new StreamReader(nginxConfStream).ReadToEnd());
        int serverIndex = 0;

        foreach (IToken extraNginxConfigToken in extraNginxConfig.GetTokens())
            if (extraNginxConfigToken is GroupToken extraNginxConfigGroupToken && extraNginxConfigGroupToken.Key.Equals("http", StringComparison.InvariantCultureIgnoreCase))
            {
                foreach (IToken serverToken in extraNginxConfigGroupToken.Tokens)
                    if (serverToken is GroupToken serverGroupToken && serverGroupToken.Key.Equals("server", StringComparison.InvariantCultureIgnoreCase))
                        ++serverIndex;

                break;
            }

        // BuiltinNginxRules 已删除：规则由 ProxyController.BuildConfig 生成并写进代理配置文件。
        NginxConfs = extraNginxConfig
            .AddOrUpdate("worker_processes", "auto")
            .AddOrUpdate("events:worker_connections", "65536")
            .AddOrUpdate("http:proxy_set_header", "Host $http_host")
            .AddOrUpdate("http:proxy_ssl_server_name", !MainPres.IsFlashing ? "on" : "off")
            .AddOrUpdate("http:proxy_buffer_size", "14k")
            .AddOrUpdate($"http:server[{serverIndex}]:listen", $"{NginxHttpPort} default_server")
            .AddOrUpdate($"http:server[{serverIndex}]:return", "https://$host$request_uri");

        // Unix 下 nginx 默认 fork 到后台，父进程立刻退出就无法拿到句柄做精确停止
        if (!OperatingSystem.IsWindows())
            NginxConfs = NginxConfs.AddOrUpdate("daemon", "off");

        foreach (List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>? cealHostRules in CealHostRulesDict.Values)
            foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp) in cealHostRules ?? [])
            {
                string serverName = "~";

                foreach ((string cealHostIncludeDomain, string cealHostExcludeDomain) in cealHostDomainPairs)
                {
                    if (cealHostIncludeDomain.StartsWith('#'))
                        continue;

                    serverName += "^" + (!string.IsNullOrWhiteSpace(cealHostExcludeDomain) ? $"(?!{cealHostExcludeDomain.Replace(".", "\\.").Replace("*", ".*")})" : string.Empty) +
                                  cealHostIncludeDomain.TrimStart('$').Replace(".", "\\.").Replace("*", ".*") + "$|";
                }

                if (serverName == "~")
                    continue;

                ++serverIndex;

                // 内置引擎的规则不再在这里生成（已移到 ProxyController.BuildConfig），
                // 但外部 nginx 仍然吃下面这段 nginx.conf 的 server{} 文本。
                NginxConfs = NginxConfs
                    .AddOrUpdate($"http:server[{serverIndex}]:server_name", serverName.TrimEnd('|'))
                    .AddOrUpdate($"http:server[{serverIndex}]:listen", $"{NginxHttpsPort} ssl http2")
                    .AddOrUpdate($"http:server[{serverIndex}]:ssl_certificate", Path.GetFileName(MainConst.NginxCertPath))
                    .AddOrUpdate($"http:server[{serverIndex}]:ssl_certificate_key", Path.GetFileName(MainConst.NginxKeyPath))
                    .AddOrUpdate($"http:server[{serverIndex}]:location", "/", true)
                    .AddOrUpdate($"http:server[{serverIndex}]:location:proxy_pass", $"https://{cealHostIp}");

                // SNI 为空串时用规则首个域名（去掉通配符/前导 $）兜底，
                // 否则上游收不到可识别的 SNI 会直接断连。
                string? cealHostSniEffective = cealHostSni is null ? null :
                    !string.IsNullOrEmpty(cealHostSni) ? cealHostSni :
                    (cealHostDomainPairs.FirstOrDefault(p => !p.cealHostIncludeDomain.StartsWith('#')).cealHostIncludeDomain
                        ?? string.Empty).TrimStart('$', '*', '.');

                NginxConfs = cealHostSniEffective is null ?
                    NginxConfs.AddOrUpdate($"http:server[{serverIndex}]:proxy_ssl_server_name", "off") :
                    NginxConfs.AddOrUpdate($"http:server[{serverIndex}]:proxy_ssl_name", cealHostSniEffective);
            }
    }
    private async void MihomoConfWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        // 配置是拼给 agent 用的，特权在 agent 身上，不在 GUI 身上。
        // 这里以前按「GUI 是不是管理员」拦 Windows：新模型下 GUI 故意不提权，
        // 于是直接 return，NginxConfs / MihomoConfs 保持空，agent 拿到一份没有规则的
        // 配置 —— 全局伪造点了自然「没有作用」，而且一声不响。
        if (!MainPres.IsAgentReady)
            return;

        if (!MainPres.IsMihomoExist && !MainPres.IsComihomoExist)
            return;

        try
        {
            if (!File.Exists(MainConst.MihomoConfPath))
                await File.Create(MainConst.MihomoConfPath).DisposeAsync();

            MihomoMixedPort = 7880;

            foreach (IPEndPoint activeTcpListener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (activeTcpListener.Port == MihomoMixedPort)
                    MihomoMixedPort++;
                else if (activeTcpListener.Port > MihomoMixedPort)
                    break;

            await using FileStream mihomoConfStream = new(MainConst.MihomoConfPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Dictionary<string, object> mihomoConfDict = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Dictionary<string, object>>(ExtraMihomoConfs = await new StreamReader(mihomoConfStream).ReadToEndAsync()) ?? [];
            Dictionary<string, object> hostsMihomoConfDict = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Dictionary<string, object>>(ExtraMihomoConfs = await new StreamReader(mihomoConfStream).ReadToEndAsync()) ?? [];

            mihomoConfDict["mixed-port"] = hostsMihomoConfDict["mixed-port"] = MihomoMixedPort;

            // 见 Utils/MihomoTunRoutes：源站 IP 不进 TUN，回源少绕一层引擎。
            List<string> routeExcludeAddresses = MihomoTunRoutes.BuildRouteExcludeAddresses(
                CealHostRulesDict.Values.Where(rules => rules is not null).SelectMany(rules => rules!).Select(rule => rule.cealHostIp));

            // Linux 上没有「系统栈」可用：tun.stack 写 system 或 mixed 时，包被 auto-route 塞进 TUN
            // 之后引擎根本不接手（实测 /connections 恒为 0，debug 日志里一条 [TCP] 都没有），
            // 于是只有 hosts 伪造出的 127.0.0.1 和 route-exclude-address 里的源站 IP 能通，
            // 其余目标全卡在 SYN-SENT——这就是「google 能开、百度打不开」。换 gvisor 后引擎才真正处理 TCP。
            string tunStack = OperatingSystem.IsLinux() ? "gvisor" : "system";

            mihomoConfDict["tun"] = hostsMihomoConfDict["tun"] = new
            {
                enable = true,
                stack = tunStack,
                autoRoute = true,
                autoDetectInterface = true,
                dnsHijack = new[] { "any:53", "tcp://any:53" },
                routeExcludeAddress = routeExcludeAddresses
            };
            // 不配 dns.listen：:53 在 Linux 上和 systemd-resolved 抢端口（实测每次启动都是
            // "listen udp :53: bind: address already in use"， socket 从来没开起来），
            // 而上面 tun.dns-hijack 的 any:53 已经覆盖了劫持域名解析这条路径。
            mihomoConfDict["dns"] = new
            {
                enable = true,
                ipv6 = true,
                nameserver = MainConst.MihomoNameServers
            };
            hostsMihomoConfDict["dns"] = new
            {
                enable = true,
                ipv6 = true
            };

            MihomoConfs = new SerializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance).Build().Serialize(mihomoConfDict);

            Dictionary<string, string> mihomoHostsDict = [];

            foreach (List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>? cealHostRules in CealHostRulesDict.Values)
                foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, _, _) in cealHostRules ?? [])
                    foreach ((string cealHostIncludeDomain, _) in cealHostDomainPairs)
                    {
                        string cealHostIncludeDomainWithoutWildcard = cealHostIncludeDomain.TrimStart('$').TrimStart('*').TrimStart('.');

                        if (cealHostIncludeDomain.StartsWith('#') || string.IsNullOrWhiteSpace(cealHostIncludeDomainWithoutWildcard))
                            continue;

                        if (cealHostIncludeDomain.TrimStart('$').StartsWith('*'))
                        {
                            // 必须用 '+.'（mihomo 的 domain-suffix 语法），不能用 '*.'。
                            // '*.' 只匹配**一级**子域：'*.google.com' 能命中 accounts.google.com，
                            // 但命中不了 signaler-pa.clients6.google.com（两级）。Google 大量域名
                            // 都是两级以上，漏掉的会走 mihomo DIRECT 去连真实 IP —— 在国内必然失败，
                            // 于是会话被劈成两半（部分走代理、部分直连被墙），
                            // 表现为页面能开但 batchexecute 被判异常流量。
                            mihomoHostsDict.TryAdd($"+.{cealHostIncludeDomainWithoutWildcard}", "127.0.0.1");
                            mihomoHostsDict.TryAdd($"*.{cealHostIncludeDomainWithoutWildcard}", "127.0.0.1");

                            if (cealHostIncludeDomain.TrimStart('$').StartsWith("*."))
                                continue;
                        }

                        mihomoHostsDict.TryAdd(cealHostIncludeDomainWithoutWildcard, "127.0.0.1");
                    }

            mihomoConfDict["hosts"] = hostsMihomoConfDict["hosts"] = mihomoHostsDict;

            ComihomoConfs = new SerializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance).Build().Serialize(mihomoConfDict);
            HostsComihomoConfs = new SerializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance).Build().Serialize(hostsMihomoConfDict);
        }
        catch { ComihomoConfs = HostsComihomoConfs = MihomoConfs = string.Empty; }
    }
    private async Task<bool> WaitForProxyReadyAsync(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                AgentResponse status = await ProxyController!.Client.StatusAsync();

                if (status.Ok && status.Status?.Running == true)
                    return true;
            }
            catch
            {
                // agent 可能还在启动中，忽略异常继续轮询
            }

            await Task.Delay(500);
        }

        return false;
    }
    private void UpdateProxyStatusText()
    {
        if (MainPres.IsCoproxyIniting)
            MainPres.ProxyStatusText = "正在启动代理...";
        else if (MainPres.IsNginxIniting)
            MainPres.ProxyStatusText = "正在启动代理...";
        else if (MainPres.IsProxyRunning)
        {
            string engine = MainPres.IsConginxRunning ? "mihomo+nginx" :
                MainPres.IsNginxRunning ? "nginx" :
                MainPres.IsMihomoRunning ? "mihomo" : MainPres.ProxyEngineName;
            MainPres.ProxyStatusText = $"代理运行中 ({engine})";
        }
        else
            MainPres.ProxyStatusText = "代理未启动";
    }
    private void MainWin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.W)
            ExitApplication();
    }
}