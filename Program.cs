using Avalonia;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sheas_Cealer_Nix;

internal sealed class Program
{
    private const uint MbIconError = 0x10;

    [STAThread]
    private static void Main(string[] args)
    {
        // 必须在任何读 MainConst 路径的代码之前跑：把旧 bundle 里的配置/证书搬到数据目录
        DataMigration.Run();

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { ReportFatal(ex); }
    }

    // 走到这里消息循环已经结束了：Avalonia 的弹窗永远画不出来，await 它更是永久卡死
    // ——实测症状就是「弹出一个没有文字的框，进程挂住不退」。只能落到系统消息框，
    // 它自带模态循环，不依赖 Avalonia；完整异常同时写进日志，方便事后排查。
    private static void ReportFatal(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(MainConst.DataDir);
            File.AppendAllText(MainConst.GuiErrorLogPath, $"{DateTime.Now:O} {ex}{Environment.NewLine}");
        }
        catch { }

        string text = $"Error: {ex.Message}";

        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, text, MainConst.NotifyIconText, MbIconError);
        else
            Console.Error.WriteLine(text);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
