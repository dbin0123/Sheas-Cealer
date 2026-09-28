using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sheas_Cealer_Nix.Utils;

internal static class PlatformHelper
{
    private static bool IsWindows => OperatingSystem.IsWindows();
    private static bool IsMacOS => OperatingSystem.IsMacOS();

    // Unix 的进程名就是文件名, Windows 才是去掉扩展名的文件名
    internal static string ProcName(string exePath) => IsWindows ? Path.GetFileNameWithoutExtension(exePath) : Path.GetFileName(exePath);

    // 从压缩包或浏览器下载来的 mihomo / nginx 可能没有执行位,
    // Process.Start 会直接抛 UnauthorizedAccessException, 启动前补上即可。
    internal static void EnsureExecutable(string exePath)
    {
        if (IsWindows || !File.Exists(exePath))
            return;

        try
        {
            File.SetUnixFileMode(exePath,
                File.GetUnixFileMode(exePath) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    internal static void Open(string pathOrUrl)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        else
            // 注意 Start 的第一个参数是可执行文件名, 这里必须带上 open / xdg-open,
            // 否则会把 ShellOpenArgs 的首项（macOS 是 "-a"）当成程序名去启动。
            Start([ShellOpenCommand, .. ShellOpenArgs(pathOrUrl)]);
    }
    internal static void OpenAsAdmin(string pathOrUrl)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true, Verb = "RunAs" });
        else if (IsMacOS)
        {
            // GUI 程序没有终端, sudo 拿不到密码, 交给 osascript 弹原生授权框
            string script = $"do shell script \"{ShellCommand(pathOrUrl).Replace("\"", "\\\"")}\" with administrator privileges";

            Start("osascript", "-e", script);
        }
        else
            Start(["sudo", ShellOpenCommand, .. ShellOpenArgs(pathOrUrl)]);
    }

    internal static void FlushDns()
    {
        if (IsWindows)
            DnsFlusher.FlushDns();
        else if (IsMacOS)
        {
            Start("dscacheutil", "-flushcache");
            Start("killall", "-HUP", "mDNSResponder");
        }
    }

    private static string ShellOpenCommand => IsMacOS ? "open" : "xdg-open";
    private static bool IsUri(string pathOrUrl) => Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri? uri) && !uri.IsFile;

    // macOS 对无扩展名的配置类文件没有默认关联程序, 直接交给 TextEdit
    private static string[] ShellOpenArgs(string pathOrUrl)
    {
        if (IsMacOS && !IsUri(pathOrUrl) && !Directory.Exists(pathOrUrl))
            return ["-a", "TextEdit", pathOrUrl];

        return [pathOrUrl];
    }

    private static string ShellCommand(string pathOrUrl) => $"{ShellOpenCommand} {string.Join(' ', ShellOpenArgs(pathOrUrl).Select(ShellQuote))}";
    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

    private static void Start(params string[] args)
    {
        ProcessStartInfo startInfo = new(args[0]) { UseShellExecute = false };

        foreach (string arg in args.Skip(1))
            startInfo.ArgumentList.Add(arg);

        Process.Start(startInfo);
    }
}
