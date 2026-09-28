using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Props;

namespace Sheas_Cealer_Nix.Preses;

internal partial class GlobalPres : ObservableObject
{
    internal GlobalPres()
    {
        //IsLightTheme = Settings.Default.IsLightTheme switch
        //{
        //    -1 => null,
        //    0 => false,
        //    1 => true,
        //    _ => throw new UnreachableException()
        //};
    }

    [ObservableProperty]
    private static bool? isLightTheme = null;
    partial void OnIsLightThemeChanged(bool? value)
    {
        if (Application.Current is not { } currentApp)
            return;

        currentApp.RequestedThemeVariant = value switch
        {
            true => ThemeVariant.Light,
            false => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        Settings.Default.IsLightTheme = (sbyte)(value.HasValue ? value.Value ? 1 : 0 : -1);
        Settings.Default.Save();
    }

    [ObservableProperty]
    private static Color accentForegroundColor = AboutConst.AccentBlueColor;
}