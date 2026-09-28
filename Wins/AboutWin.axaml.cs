using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Preses;
using Sheas_Cealer_Nix.Utils;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Wins;

public partial class AboutWin : Window
{
    private readonly AboutPres AboutPres;
    private readonly HttpClient AboutClient = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

    public AboutWin()
    {
        DataContext = AboutPres = new();

        InitializeComponent();
    }
    //private void AboutWin_SourceInitialized(object sender, EventArgs e)
    //{
    //    IconRemover.RemoveIcon(this);
    //    BorderThemeSetter.SetBorderTheme(this, AboutPres.IsLightTheme);
    //}
    private async void AboutWin_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Run(async () =>
        {
            try
            {
                AboutClient.DefaultRequestHeaders.Add("User-Agent", AboutConst.ReleaseApiUserAgent);

                JsonElement releaseInfoObject = JsonDocument.Parse(await Http.GetAsync(AboutConst.ReleaseApiUrl, AboutClient)).RootElement;

                AboutClient.DefaultRequestHeaders.Clear();

                foreach (JsonProperty releaseInfoContent in releaseInfoObject.EnumerateObject())
                    if (releaseInfoContent.Name == "name" && releaseInfoContent.Value.ToString() != AboutConst.VersionButtonVersionContent)
                        AboutPres.IsSheasCealerUtd = false;
            }
            catch { }
        });
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;

        if (senderButton == VersionButton)
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, $"{AboutConst._ReleasePagePasswordLabel} 3wnj").ShowWindowDialogAsync(this);

        string processStartUrl = senderButton == EmailButton ? "mailto:" : string.Empty + ToolTip.GetTip(senderButton);

        try { PlatformHelper.Open(processStartUrl); }
        catch (UnauthorizedAccessException) { PlatformHelper.OpenAsAdmin(processStartUrl); }
    }

    private void AboutWin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}