using Avalonia.Data.Converters;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Globalization;

namespace Sheas_Cealer_Nix.Convs;

internal class MainSettingsModeButtonContentConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        MainConst.SettingsMode settingsMode = (MainConst.SettingsMode)value;

        // 这颗芯片写的是「点下去会切到哪个模式」，当前模式名在输入框的水印上。
        // 只写一个模式名会被读成标题（左边芯片说「额外参数」、框里是上游链接，看着就是串位），
        // 所以统一套一层「切换到 {0}」，让它读起来是个动作而不是标签。
        string targetMode = settingsMode switch
        {
            MainConst.SettingsMode.BrowserPathMode => MainConst.SettingsModeButtonUpstreamUrlContent,
            MainConst.SettingsMode.UpstreamUrlMode => MainConst.SettingsModeButtonExtraArgsContent,
            MainConst.SettingsMode.ExtraArgsMode => MainConst.SettingsModeButtonNginxEngineContent,
            MainConst.SettingsMode.NginxEngineMode => MainConst.SettingsModeButtonJavaTrustContent,
            MainConst.SettingsMode.JavaTrustMode => MainConst.SettingsModeButtonBrowserPathContent,
            _ => throw new NotImplementedException()
        };

        return string.Format(MainConst.SettingsModeSwitchFormat, targetMode);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}