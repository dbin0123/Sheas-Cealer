using Avalonia;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix;

internal sealed class Program
{
    private const uint MbIconError = 0x10;

    [STAThread]
    private static void Main(string[] args)
    {
        // 卸载模式必须在 DataMigration 之前分诊：迁移会建数据目录，
        // 一边删一边建就等于永远卸不干净。
        if (Array.Exists(args, arg => arg == "--uninstall"))
        {
            // --purge-user-data 带走数据目录（用户自己维护的上游规则、证书、日志）。
            // deb 只在 purge 时传，Windows 的卸载钩子在用户确认后由安装器自己删这一份
            // （agent 只还原机器级状态）—— 语义对齐各平台的卸载惯例，
            // 而不是让一次「remove」悄悄丢掉用户数据。
            Environment.ExitCode = RunUninstall(Array.Exists(args, arg => arg == "--purge-user-data")).GetAwaiter().GetResult();

            return;
        }

        // 必须在任何读 MainConst 路径的代码之前跑：把旧 bundle 里的配置/证书搬到数据目录
        DataMigration.Run();

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { ReportFatal(ex); }
    }

    // 控制台子系统在 Windows 上是 WinExe（不占命令行窗口），从终端跑 --uninstall 时
    // 输出仍然走 stdout，安装包脚本能看到；这里不写任何文件，卸载流程不该留下新残留。
    private static async Task<int> RunUninstall(bool purgeUserData)
    {
        try
        {
            foreach (string line in await UserUninstaller.RunAsync(purgeUserData))
                Console.WriteLine(line);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"uninstall failed: {ex}");

            return 1;
        }
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
