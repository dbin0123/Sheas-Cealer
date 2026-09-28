using System;
using System.IO;

namespace Cealing_Core;

/// <summary>
/// 应用数据目录解析。二进制留在 bundle 里（<c>.app/Contents/MacOS</c>），
/// 所有会被改写的文件（配置、证书、日志、nginx/mihomo 的 conf）放这里。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Sheas-Cealer-Nix";

    /// <summary>
    /// 平台约定的用户数据目录：
    /// macOS <c>~/Library/Application Support/Sheas-Cealer-Nix</c>、
    /// Windows <c>%AppData%\Sheas-Cealer-Nix</c>、
    /// Linux <c>${XDG_CONFIG_HOME:-$HOME/.config}/Sheas-Cealer-Nix</c>。
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

            if (OperatingSystem.IsMacOS())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", AppFolderName);

            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

            if (!string.IsNullOrWhiteSpace(xdg))
                return Path.Combine(xdg, AppFolderName);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", AppFolderName);
        }
    }

    public static string EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);

        return DataDir;
    }

    /// <summary>
    /// 把比较两条路径的差异消掉：macOS 上 <c>/var</c> 是 <c>/private/var</c> 的软链，
    /// 大小写在 Windows 上也不敏感，直接比字符串会误判成「不是同一个 agent」。
    /// </summary>
    public static string Normalize(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
            // 尾部分隔符两侧都要清：C:\a\b 与 C:\a\b\ 是同一个目录。之前只在 Unix 分支清，
            // Windows 的 agent 身份握手会把「同一个 --config」判成路径不一致，
            // 于是复用不了正在跑的 agent，还报 config not found。
            return Path.TrimEndingDirectorySeparator(path).ToLowerInvariant();

        try
        {
            return Path.TrimEndingDirectorySeparator(new FileInfo(path).FullName);
        }
        catch (Exception)
        {
            return Path.TrimEndingDirectorySeparator(path);
        }
    }
}
