using Cealing_Core;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 让提权进程能用上应用自带的可执行文件。
/// </summary>
/// <remarks>
/// AppImage 之类的 bundle 挂在 FUSE 上，而 FUSE 的访问控制只认「挂载用户」：没有 allow_other 时
/// 其它 uid 统统 EACCES，root 也不例外。agent 是 pkexec 起来的 root 进程，它 exec 自己、
/// 再 exec mihomo/nginx，全都撞在这条规则上，症状就是日志里那行
/// <c>/usr/bin/sh: 1: /tmp/.mount_xxx/Cealing-Agent: Permission denied</c>。
/// 普通用户身份的 GUI 读得到 bundle，所以由它把二进制复制到真实文件系统上的目录，再让 root 跑副本。
/// 复制是「全量兜底」：不去推断 agent 到底需要哪些 dll/so（自包含发布产物本来就在一起），
/// 少猜一类依赖漏拷贝的风险，代价只是每个版本一次的磁盘写入。
/// </remarks>
internal static class BinaryStager
{
    internal sealed record StageResult(bool Ok, string? Error);

    // 只有复制成功后才赋值：在那之前 BinPath 仍然指向 bundle，启动时那些 File.Exists 探测
    // （MainPres 的引擎可用性、NginxFinder 的候选路径）才能拿到正确结果。
    internal static string? StagedDir { get; private set; }

    private static readonly object Gate = new();
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private static readonly UnixFileMode DataFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static Task<StageResult>? _stageTask;
    private static bool? _needsStaging;

    /// <summary>
    /// 返回 null 表示 root 侧的二进制已就绪；否则是给用户看的失败原因。
    /// </summary>
    internal static async Task<string?> EnsureStagedAsync()
    {
        if (StagedDir is not null || !NeedsStaging())
            return null;

        string stagedDir = AppPaths.StagedBinDir(VersionTag);
        Task<StageResult> task;

        lock (Gate)
            task = _stageTask ??= Task.Run(() => StageBundle(MainConst.AppDir, stagedDir));

        StageResult result = await task;

        // 复制失败（磁盘满、目录不可写）不记住结果，下一次点击还能重试；
        // 成功才把目录翻过去，并作废指向旧目录的路径缓存。
        if (!result.Ok)
        {
            lock (Gate)
                _stageTask = null;

            return result.Error;
        }

        StagedDir = stagedDir;
        NginxFinder.Invalidate();

        return null;
    }

    private static bool NeedsStaging()
    {
        lock (Gate)
            return _needsStaging ??= !OperatingSystem.IsWindows()
                && IsRootBlockedBundle(MainConst.AppDir, ReadMountInfo());
    }

    internal static StageResult StageBundle(string appDir, string stagedDir)
    {
        string stampPath = Path.Combine(stagedDir, "staged-from-bundle");

        try
        {
            string stamp = BuildStamp(appDir);

            // 版本目录里已经是同一份 bundle 的副本，就别再搬几百 MB。
            if (File.Exists(stampPath) && File.ReadAllText(stampPath).Trim() == stamp)
                return new StageResult(true, null);

            Directory.CreateDirectory(stagedDir);

            foreach (FileInfo source in new DirectoryInfo(appDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                // 调试符号没人 exec；软链（.DirIcon、图标）指回 bundle 里，复制了也用不上，
                // 而且遇到断链会直接抛。
                if (source.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    source.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = Path.GetRelativePath(appDir, source.FullName);
                string target = Path.Combine(stagedDir, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                source.CopyTo(target, overwrite: true);

                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, ModeFor(relative, source));
            }

            File.WriteAllText(stampPath, stamp);
            PruneOtherVersions(stagedDir);

            return new StageResult(true, null);
        }
        catch (Exception ex)
        {
            return new StageResult(false, $"无法把可执行文件复制到提权进程可读的目录（{stagedDir}）：{ex.Message}");
        }
    }

    /// <summary>
    /// bundle 所在目录是否落在「root 读不到」的挂载上：最长前缀匹配的挂载点是 FUSE 且没有 allow_other。
    /// </summary>
    internal static bool IsRootBlockedBundle(string appDir, IEnumerable<string> mountInfoLines)
    {
        // mountinfo 里的路径永远以 '/' 分隔，与宿主平台无关，所以这里不借 AppPaths.Normalize
        // ——它按宿主语义补盘符、换分隔符，Windows 上会把 "/tmp/.mount_xxx" 变成 "D:\tmp\..."。
        string bundle = TrimTrailingSlash(appDir.Replace('\\', '/'));
        (string Mount, string FsType, string Options)? best = null;

        foreach (string line in mountInfoLines)
        {
            if (!TryParseMountInfo(line, out string mountPoint, out string fsType, out string options))
                continue;

            mountPoint = TrimTrailingSlash(mountPoint);

            // 尾部补分隔符时不能给 "/" 再加一个，否则拼出 "//" 什么也匹配不上。
            string prefix = mountPoint.EndsWith('/') ? mountPoint : mountPoint + "/";

            if (bundle != mountPoint && !bundle.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            // / 也是 bundle 的前缀，必须取最深的那条挂载点，否则会被底层的 ext4 覆盖掉判断。
            if (best is null || mountPoint.Length > best.Value.Mount.Length)
                best = (mountPoint, fsType, options);
        }

        return best is not null && IsFuse(best.Value.FsType) && !best.Value.Options.Split(',').Contains("allow_other");
    }

    private static string TrimTrailingSlash(string path)
    {
        int end = path.Length;

        while (end > 1 && path[end - 1] == '/')
            end--;

        return path[..end];
    }

    internal static bool TryParseMountInfo(string line, out string mountPoint, out string fsType, out string options)
    {
        mountPoint = string.Empty;
        fsType = string.Empty;
        options = string.Empty;

        string[] fields = line.Split(' ');
        int separator = Array.IndexOf(fields, "-");

        if (separator < 0 || fields.Length < separator + 4)
            return false;

        mountPoint = UnescapeMountPath(fields[4]);
        fsType = fields[separator + 1];

        // allow_other 可能记在挂载选项（fields[5]）也可能记在超级块选项上，两边都算。
        options = fields[5] + "," + fields[separator + 3];

        return true;
    }

    // 挂载表用八进制转义表示路径里的空格和制表符（\040、\011），不还原就没法和本地路径比。
    private static string UnescapeMountPath(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
            return value;

        StringBuilder builder = new(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length &&
                value[i + 1] is >= '0' and <= '7' && value[i + 2] is >= '0' and <= '7' && value[i + 3] is >= '0' and <= '7')
            {
                builder.Append((char)Convert.ToByte(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
                builder.Append(value[i]);
        }

        return builder.ToString();
    }

    // fuse / fuseblk / fuse.gvfsd-fuse 都受挂载用户限制；fusectl 之类不会成为 bundle 的挂载点。
    private static bool IsFuse(string fsType) =>
        fsType is "fuse" or "fuseblk" || fsType.StartsWith("fuse.", StringComparison.Ordinal);

    private static IEnumerable<string> ReadMountInfo()
    {
        try
        {
            return File.ReadAllLines("/proc/self/mountinfo");
        }
        catch
        {
            return [];
        }
    }

    // 同版本号但重装过（开发机反复打包）时，bundle 里 agent 的大小/时间会变，靠它触发重复制。
    private static string BuildStamp(string appDir)
    {
        try
        {
            FileInfo agent = new(Path.Combine(appDir, "Cealing-Agent"));

            return $"{agent.Length}|{agent.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static UnixFileMode ModeFor(string relativePath, FileInfo source)
    {
        // File.Copy 不保留 mode，副本会按 umask 落成 0644 —— root 拿到照样执行不了，必须显式补。
        // 判断依据沿用 bundle 的模式；另外无扩展名的文件（apphost、Cealing-Mihomo、createdump）
        // 和 .so 天生就要能执行，打包丢位时在这里兜住。
        UnixFileMode mode = source.UnixFileMode;
        string name = Path.GetFileName(relativePath);

        bool executable = mode.HasFlag(UnixFileMode.UserExecute) ||
                          mode.HasFlag(UnixFileMode.GroupExecute) ||
                          mode.HasFlag(UnixFileMode.OtherExecute) ||
                          !name.Contains('.', StringComparison.Ordinal) ||
                          name.EndsWith(".so", StringComparison.OrdinalIgnoreCase);

        return executable ? ExecutableMode : DataFileMode;
    }

    private static void PruneOtherVersions(string stagedDir)
    {
        string? binRoot = Path.GetDirectoryName(stagedDir);

        if (binRoot is null)
            return;

        foreach (string other in Directory.GetDirectories(binRoot))
            if (AppPaths.Normalize(other) != AppPaths.Normalize(stagedDir))
                try
                {
                    Directory.Delete(other, recursive: true);
                }
                catch
                {
                    // 上一个版本的副本删不掉只是多占磁盘，不能因此判定本次启动失败。
                }
    }

    private static string VersionTag => typeof(BinaryStager).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
