using Microsoft.Win32;
using System;
using System.IO;
using System.Xml.Linq;

namespace Sheas_Cealer_Nix.Utils;

// 开机自启（登录时启动）：
//   Windows -> HKCU\Software\Microsoft\Windows\CurrentVersion\Run
//   macOS   -> ~/Library/LaunchAgents/<bundle id>.plist（RunAtLoad，下次登录生效）
//   Linux   -> ~/.config/autostart/sheas-cealer-nix.desktop（XDG Autostart）
internal static class AutoStartManager
{
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "Sheas-Cealer-Nix";
    private const string MacBundleId = "io.github.projectsheascealer.nix";

    internal static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
            using (RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath))
                return runKey?.GetValue(RegistryValueName) is not null;
        else if (OperatingSystem.IsMacOS())
            return File.Exists(MacPlistPath);
        else
            return File.Exists(LinuxDesktopPath);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath)!;

            if (enabled)
                runKey.SetValue(RegistryValueName, $"\"{LaunchTarget}\"", RegistryValueKind.String);
            else
                runKey.DeleteValue(RegistryValueName, false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
                File.WriteAllText(MacPlistPath, MacPlistContent);
            }
            else if (File.Exists(MacPlistPath))
                File.Delete(MacPlistPath);
        }
        else
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
                File.WriteAllText(LinuxDesktopPath, LinuxDesktopContent);
            }
            else if (File.Exists(LinuxDesktopPath))
                File.Delete(LinuxDesktopPath);
        }
    }

    // AppImage 下 ProcessPath 是 /tmp/.mount_xxx 的一次性挂载点，重启后失效，
    // 只有 APPIMAGE 环境变量指向磁盘上真正的 AppImage 文件。
    private static string LaunchTarget =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImagePath ? appImagePath :
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the executable path.");

    private static string MacPlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{MacBundleId}.plist");
    private static string LinuxDesktopPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "autostart", "sheas-cealer-nix.desktop");

    private static string MacPlistContent
    {
        get
        {
            XDocument doc = new(
                new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
                new XElement("plist",
                    new XAttribute("version", "1.0"),
                    new XElement("dict",
                        new XElement("key", "Label"),
                        new XElement("string", MacBundleId),
                        new XElement("key", "ProgramArguments"),
                        new XElement("array", new XElement("string", LaunchTarget)),
                        new XElement("key", "RunAtLoad"),
                        new XElement("true"))));

            return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{Environment.NewLine}{doc}";
        }
    }

    private static string LinuxDesktopContent =>
        $$"""
        [Desktop Entry]
        Type=Application
        Name=Sheas Cealer Nix
        Comment=SNI spoofing tool
        Exec={{"\"" + LaunchTarget.Replace(@"\", @"\\").Replace("\"", "\\\"") + "\""}}
        Icon=sheas-cealer-nix
        Terminal=false
        StartupWMClass=Sheas-Cealer-Nix
        """;
}
