using Avalonia;
using Sheas_Cealer_Nix.Preses;
using Sheas_Cealer_Nix.Props;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Wins;
using System;
using System.IO;
using System.Linq;

namespace Sheas_Cealer_Nix;

internal partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 恢复上次选择的配色：-1 跟随系统 / 0 深色 / 1 浅色
        RequestedThemeVariant = SettingsPres.ThemeVariantFor(Settings.Default.IsLightTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Todo: Check x11-utils & xdg-utils environment for Linux.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            #region Upgrade Settings
            //if (Settings.Default.IsUpgradeRequired)
            //{
            //    Settings.Default.Upgrade();
            //    Settings.Default.IsUpgradeRequired = false;
            //    Settings.Default.Save();
            //}
            #endregion Upgrade Settings

            #region Primary Color
            //PaletteHelper paletteHelper = new();
            //Theme newTheme = paletteHelper.GetTheme();
            //Color newPrimaryColor = Color.FromRgb(Settings.Default.PrimaryColor.R, Settings.Default.PrimaryColor.G, Settings.Default.PrimaryColor.B);

            //newTheme.SetPrimaryColor(newPrimaryColor);
            //paletteHelper.SetTheme(newTheme);
            #endregion Primary Color

            #region Background Color
            //if (Environment.OSVersion.Version.Build < 22000)
            //{
            //    Style newWindowStyle = new(typeof(Window), Current.Resources["CommonWindow"] as Style);

            //    newWindowStyle.Setters.Add(new Setter(Window.BackgroundProperty, new DynamicResourceExtension("MaterialDesignBackground")));
            //    Current.Resources["CommonWindow"] = newWindowStyle;
            //}
            #endregion Background Color

            #region Foreground Color
            //Style newButtonStyle = new(typeof(Button), Current.Resources[typeof(Button)] as Style);
            //(Color? newForegroundColor, Color newAccentForegroundColor) = ForegroundGenerator.GetForeground(newPrimaryColor.R, newPrimaryColor.G, newPrimaryColor.B);

            //newButtonStyle.Setters.Add(new Setter(Button.ForegroundProperty, newForegroundColor.HasValue ? new SolidColorBrush(newForegroundColor.Value) : new DynamicResourceExtension("MaterialDesignBackground")));
            //Current.Resources[typeof(Button)] = newButtonStyle;

            //new SettingsPres().AccentForegroundColor = newAccentForegroundColor;
            #endregion Foreground Color

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWin();

            // 兜底：从 async void 事件处理器逃逸的异常会经由分发器重抛，一旦逃出消息循环，
            // 界面就表现为「点了没反应、整个卡死」（Program.Main 里的补救弹窗在循环已死时永远画不出来）。
            // 接住它：写日志 + 弹可读的错误框，窗口本身继续可用。
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;

                try
                {
                    Directory.CreateDirectory(MainConst.DataDir);
                    File.AppendAllText(MainConst.GuiErrorLogPath, $"{DateTime.Now:O} {args.Exception}{Environment.NewLine}");
                }
                catch { }

                if (desktop.MainWindow is null)
                    return;

                _ = MessageBoxManager.GetMessageBoxStandard(string.Empty, $"Error: {args.Exception.Message}", ButtonEnum.Ok)
                        .ShowWindowDialogAsync(desktop.MainWindow);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // Remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}