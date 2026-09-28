using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Sheas_Cealer_Nix.Consts;
using System;
using System.IO;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// Windows / Linux 的系统托盘。macOS 的菜单栏图标由外部 StatusBarHelper 二进制提供
/// （csproj 里编译条件是 IsOSPlatform('OSX')），其余平台以前压根没有托盘入口，
/// 而点 X 只是 Hide() —— 于是窗口消失、进程活着、再也叫不回来，只能任务管理器杀。
/// </summary>
internal static class NativeTray
{
    // 加载失败（资源没打进去、格式不支持）绝不能把退出入口一起带走，所以整段是可失败的。
    internal static bool Attach(Application app, Action<string> onMenuClicked)
    {
        try
        {
            TrayIcon tray = new()
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Sheas-Cealer-Nix/Sheas-Cealer-Nix-Logo.ico"))),
                ToolTipText = MainConst.NotifyIconText,
                Menu = BuildMenu(onMenuClicked),
                IsVisible = true
            };

            // 单击 = 叫回窗口。没有这一步的话用户只能靠右键菜单。
            tray.Clicked += (_, _) => onMenuClicked(MainConst.TrayMenuShowWindow);

            TrayIcon.SetIcons(app, new TrayIcons { tray });

            return true;
        }
        catch (Exception ex)
        {
            // 桌面环境没有 StatusNotifier 支持时走这里。调用方据此把「关闭」变成真退出。
            // 必须落盘：这次的教训就是「静默降级」让人只能靠猜——没有托盘还照样 Hide()，
            // 进程就变成了任务管理器里才看得见的残留。
            try
            {
                Directory.CreateDirectory(MainConst.DataDir);
                File.AppendAllText(MainConst.GuiErrorLogPath, $"{DateTime.Now:O} native tray unavailable, close will quit instead: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
            }

            return false;
        }
    }

    private static NativeMenu BuildMenu(Action<string> onMenuClicked)
    {
        NativeMenu menu = new();

        foreach (string item in new[]
        {
            MainConst.TrayMenuShowWindow,
            MainConst.TrayMenuStartBrowser,
            MainConst.TrayMenuStartGlobal,
            MainConst.TrayMenuStopGlobal,
            MainConst.TrayMenuUpdateUpstream,
            MainConst.TrayMenuQuit
        })
        {
            NativeMenuItem menuItem = new() { Header = item };
            string key = item;

            menuItem.Click += (_, _) => onMenuClicked(key);
            menu.Add(menuItem);
        }

        return menu;
    }
}
