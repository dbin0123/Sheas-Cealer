using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Props;
using Sheas_Cealer_Nix.Utils;
using System;
using System.Diagnostics;
using System.IO;
using File = System.IO.File;

namespace Sheas_Cealer_Nix.Preses;

internal partial class MainPres : GlobalPres
{
    internal MainPres()
    {
        string[] args = Environment.GetCommandLineArgs();

        int browserPathIndex = Array.FindIndex(args, arg => arg.Equals("-b", StringComparison.OrdinalIgnoreCase)) + 1;
        int upstreamUrlIndex = Array.FindIndex(args, arg => arg.Equals("-u", StringComparison.OrdinalIgnoreCase)) + 1;
        int extraArgsIndex = Array.FindIndex(args, arg => arg.Equals("-e", StringComparison.OrdinalIgnoreCase)) + 1;
        int nginxEngineIndex = Array.FindIndex(args, arg => arg.Equals("-n", StringComparison.OrdinalIgnoreCase)) + 1;

        BrowserPath = browserPathIndex != 0 && browserPathIndex != args.Length ? args[browserPathIndex] :
            !string.IsNullOrWhiteSpace(Settings.Default.BrowserPath) ? Settings.Default.BrowserPath :
            OperatingSystem.IsWindows() ?
            (Registry.LocalMachine.OpenSubKey(MainConst.EdgeBrowserRegistryPath)?.GetValue(string.Empty, null) ??
            Registry.LocalMachine.OpenSubKey(MainConst.ChromeBrowserRegistryPath)?.GetValue(string.Empty, null) ??
            Registry.LocalMachine.OpenSubKey(MainConst.BraveBrowserRegistryPath)?.GetValue(string.Empty, null) ??
            string.Empty).ToString()! :
            OperatingSystem.IsMacOS() ?
            Array.Find(MainConst.MacBrowserPaths, File.Exists) ?? string.Empty :
            string.Empty;

        UpstreamUrl = upstreamUrlIndex == 0 || upstreamUrlIndex == args.Length ?
            !string.IsNullOrWhiteSpace(Settings.Default.UpstreamUrl) ? Settings.Default.UpstreamUrl : MainConst.DefaultUpstreamUrl :
            args[upstreamUrlIndex];

        ExtraArgs = extraArgsIndex == 0 || extraArgsIndex == args.Length ?
            !string.IsNullOrWhiteSpace(Settings.Default.ExtraArgs) ? Settings.Default.ExtraArgs : string.Empty :
            args[extraArgsIndex];

        // 优先级：命令行 -n > 用户配置 > AutoMode
        if (nginxEngineIndex != 0 && nginxEngineIndex != args.Length &&
            Enum.TryParse(args[nginxEngineIndex], ignoreCase: true, out MainConst.NginxEngineMode parsedNginxEngineMode))
            NginxEngineMode = parsedNginxEngineMode;
        else if (Enum.IsDefined(typeof(MainConst.NginxEngineMode), Settings.Default.NginxEngineMode))
            NginxEngineMode = (MainConst.NginxEngineMode)Settings.Default.NginxEngineMode;
        else
            NginxEngineMode = MainConst.NginxEngineMode.AutoMode;

        // 构造函数里的赋值不应触发一次无意义的写盘，只有设置界面里的改动才持久化。
        NginxEngineModeLoaded = true;
    }

    private bool NginxEngineModeLoaded;

    internal ProxyEngine ResolvedProxyEngine => NginxFinder.Resolve(NginxEngineMode);

    internal bool IsBuiltinProxyEngine => ResolvedProxyEngine == ProxyEngine.Builtin;

    [ObservableProperty]
    private MainConst.SettingsMode settingsMode;

    [ObservableProperty]
    private string browserPath;
    partial void OnBrowserPathChanged(string value)
    {
        //if (!File.Exists(value))
        //    return;

        //Settings.Default.BrowserPath = value;
        //Settings.Default.Save();
    }

    [ObservableProperty]
    private string upstreamUrl;
    partial void OnUpstreamUrlChanged(string value)
    {
        //if (!MainConst.UpstreamUrlRegex().IsMatch(value))
        //    return;

        //Settings.Default.UpstreamUrl = value;
        //Settings.Default.Save();
    }

    [ObservableProperty]
    private string extraArgs;
    partial void OnExtraArgsChanged(string value)
    {
        //if (!MainConst.ExtraArgsRegex().IsMatch(value))
        //    return;

        //Settings.Default.ExtraArgs = value;
        //Settings.Default.Save();
    }

    [ObservableProperty]
    private bool isUpstreamHostUtd = true;

    [ObservableProperty]
    private MainConst.NginxEngineMode nginxEngineMode;
    partial void OnNginxEngineModeChanged(MainConst.NginxEngineMode value)
    {
        if (NginxEngineModeLoaded && Settings.Default.NginxEngineMode != (int)value)
        {
            try
            {
                Settings.Default.NginxEngineMode = (int)value;
                Settings.Default.Save();
            }
            catch { }
        }

        RefreshProxyEngine();
    }

    [ObservableProperty]
    private string resolvedNginxPath = string.Empty;

    internal void RefreshProxyEngine()
    {
        NginxFinder.Refresh();
        ResolvedNginxPath = NginxFinder.Find() ?? string.Empty;
    }

    [ObservableProperty]
    private bool isCoproxyIniting = false;

    [ObservableProperty]
    private bool isCoproxyStopping = false;

    [ObservableProperty]
    private bool isConginxExist = File.Exists(MainConst.ConginxPath);

    [ObservableProperty]
    private bool isNginxExist = File.Exists(MainConst.NginxPath);

    [ObservableProperty]
    private bool isNginxIniting = false;

    [ObservableProperty]
    private bool isConginxRunning = false;

    [ObservableProperty]
    private bool isNginxRunning = false;

    [ObservableProperty]
    private bool isComihomoExist = File.Exists(MainConst.ComihomoPath);

    [ObservableProperty]
    private bool isMihomoExist = File.Exists(MainConst.MihomoPath);

    [ObservableProperty]
    private bool isComihomoIniting = false;

    [ObservableProperty]
    private bool isMihomoIniting = false;

    [ObservableProperty]
    private bool isComihomoRunning = false;

    [ObservableProperty]
    private bool isMihomoRunning = false;

    [ObservableProperty]
    private bool isFlashing = false;

    // 下面是 agent 架构新增的状态。
    // 原来靠 Process.GetProcessesByName 判断 nginx/mihomo 是否在跑，现在进程是 agent 的子进程，
    // GUI 拿不到句柄，改为轮询 agent 上报的状态（IsProxyRunning + ProxyEngineName）。
    [ObservableProperty]
    private bool isAgentReady = false;

    [ObservableProperty]
    private bool isAgentStarting = false;

    [ObservableProperty]
    private string agentError = string.Empty;

    [ObservableProperty]
    private bool isProxyRunning = false;

    [ObservableProperty]
    private string proxyEngineName = "none";

    [ObservableProperty]
    private string proxyStatusText = "代理未启动";
}