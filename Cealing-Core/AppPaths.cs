using System;
using System.IO;
using System.Linq;

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
    public static string DataDir => DataDirForHome(CurrentHome);

    /// <summary>
    /// 同一个目录规则，但按指定的家目录算。卸载时 root 侧要清理**别人**的家目录
    /// （agent 以 root 运行，<c>Environment.GetFolderPath(UserProfile)</c> 拿到的是 /root），
    /// 装和删必须用同一套路径，否则删不到。
    /// </summary>
    public static string DataDirForHome(string home)
    {
        if (string.IsNullOrWhiteSpace(home))
            throw new ArgumentException("home must not be empty", nameof(home));

        if (OperatingSystem.IsWindows())
            return Path.Combine(home, "AppData", "Roaming", AppFolderName);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", AppFolderName);

        string? xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        return Path.Combine(
            string.IsNullOrWhiteSpace(xdgConfig) ? Path.Combine(home, ".config") : xdgConfig,
            AppFolderName);
    }

    public static string EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);

        return DataDir;
    }

    /// <summary>
    /// root 侧二进制的暂存目录根（下面按版本分子目录）。bundle 挂在 root 执行不了的文件系统上时
    /// （AppImage 的 FUSE 挂载），GUI 会把可执行文件复制到这里的真实目录再交给提权进程。
    /// </summary>
    public static string StagedBinRoot => StagedBinRootForHome(CurrentHome);

    public static string StagedBinRootForHome(string home)
    {
        if (OperatingSystem.IsMacOS())
            // macOS 暂存目录就放在数据目录里，删数据目录时一起带走。
            return Path.Combine(DataDirForHome(home), "bin");

        return Path.Combine(XdgDataHomeForHome(home), AppFolderName, "bin");
    }

    public static string StagedBinDir(string version) => Path.Combine(StagedBinRoot, version);

    /// <summary>
    /// 开机自启条目的位置（macOS 的 LaunchAgents / Linux 的 XDG autostart）。
    /// Windows 上它是 HKCU 注册表值而不是文件，返回 null。
    /// </summary>
    /// <remarks>
    /// 写入方是 GUI 的 AutoStartManager，那边按当前用户算；卸载要处理的是**别人的**家目录
    /// （root 侧善后），所以这里必须能按任意 home 复现同一条路径。两处常量改名要一起改。
    /// </remarks>
    public static string? AutostartEntryForHome(string home)
    {
        if (OperatingSystem.IsWindows())
            return null;

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "LaunchAgents", "io.github.projectsheascealer.nix.plist")
            : Path.Combine(XdgDataHomeForHome(home), "autostart", "sheas-cealer-nix.desktop");
    }

    private static string XdgDataHomeForHome(string home)
    {
        string? xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        return string.IsNullOrWhiteSpace(xdgData) ? Path.Combine(home, ".local", "share") : xdgData;
    }

    private static string CurrentHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 删除暂存的 root 侧二进制（可达数百 MB）。
    /// </summary>
    public static void DeleteStagedBin() => DeleteStagedBin(CurrentHome);

    public static void DeleteStagedBin(string home)
    {
        // Windows 上没有暂存这套机制（BinaryStager 只在 root 被 bundle 的文件系统挡住时才复制），
        // 而 StagedBinRootForHome 的默认分支是 XDG 形状的，在 Windows 上没有任何意义。
        if (OperatingSystem.IsWindows())
            return;

        string bin = StagedBinRootForHome(home);

        if (Directory.Exists(bin))
            Directory.Delete(bin, recursive: true);

        // Linux 上暂存目录独占 ${XDG_DATA_HOME}/Sheas-Cealer-Nix，删完 bin 只剩空壳；
        // macOS 的暂存嵌在数据目录里，那个壳就是用户的配置目录本身，绝不能碰。
        string? parent = Path.GetDirectoryName(bin);

        if (!OperatingSystem.IsMacOS()
            && parent is not null
            && string.Equals(Path.GetFileName(parent), AppFolderName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
    }

    /// <summary>
    /// 删除本应用的目录（数据目录或暂存目录根）。
    /// 只允许删以 <see cref="AppFolderName"/> 结尾的路径：卸载入口的调用方（安装包脚本、
    /// 手敲的 --user-home）传错路径时这里必须拒绝，而不是把别人的目录连锅端走。
    /// </summary>
    public static void DeleteAppFolder(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!string.Equals(Path.GetFileName(trimmed), AppFolderName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"refuse to delete {trimmed}: not a {AppFolderName} folder", nameof(path));

        if (Directory.Exists(trimmed))
            Directory.Delete(trimmed, recursive: true);
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
