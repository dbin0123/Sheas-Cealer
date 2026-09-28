using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Sheas_Cealer_Nix.Utils;

public static class StatusBarHelper
{
    private static Process? _process;
    private static Window? _window;

    public static bool IsRunning => _process is { HasExited: false };
    public static event Action<string>? MenuClicked;

    public static void Start(Window window)
    {
        if (_process is { HasExited: false })
            return;

        _window = window;

        string helperPath = Path.Combine(AppContext.BaseDirectory, "StatusBarHelper");
        if (!File.Exists(helperPath))
        {
            Console.WriteLine("StatusBarHelper binary not found at " + helperPath);
            return;
        }

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _process.Start();
            _ = Task.Run(ReadMenuClicksAsync);
            Console.WriteLine("StatusBarHelper started (PID " + _process.Id + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine("StatusBarHelper start error: " + ex.Message);
        }
    }

    private static async Task ReadMenuClicksAsync()
    {
        if (_process == null) return;

        try
        {
            StreamReader reader = _process.StandardOutput;
            while (!_process.HasExited)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null) break;

                if (line.StartsWith("click:"))
                {
                    string item = line["click:".Length..];
                    MenuClicked?.Invoke(item);
                }
            }
        }
        catch { }
    }

    public static void Quit()
    {
        if (_process is not { HasExited: false }) return;

        try
        {
            _process.StandardInput.WriteLine("quit");
            _process.StandardInput.Flush();
        }
        catch { }

        _process = null;
    }

    public static void ShowWindow()
    {
        if (_window == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _window.Show();
            _window.Activate();
        });
    }
}
