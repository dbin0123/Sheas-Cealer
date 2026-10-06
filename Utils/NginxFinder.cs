using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

internal enum ProxyEngine
{
    External,
    Builtin
}

internal static partial class NginxFinder
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly Version MinVersion = new(1, 15, 0);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<bool, string?> PathCache = new() { { false, null }, { true, null } };
    private static readonly HashSet<bool> CacheResolved = [];
    private static readonly Dictionary<string, bool> CompatibleCache = [];
    private static DateTime LastRefresh = DateTime.MinValue;

    private static readonly string[] CommonPaths = OperatingSystem.IsWindows() ?
        [@"C:\nginx\nginx.exe", @"C:\Program Files\nginx\nginx.exe", @"C:\ProgramData\chocolatey\bin\nginx.exe"] :
        OperatingSystem.IsMacOS() ?
        ["/opt/homebrew/bin/nginx", "/opt/homebrew/sbin/nginx", "/usr/local/bin/nginx", "/usr/local/sbin/nginx", "/opt/local/bin/nginx", "/opt/local/sbin/nginx"] :
        ["/usr/sbin/nginx", "/usr/local/sbin/nginx", "/usr/bin/nginx", "/usr/local/bin/nginx"];

    internal static ProxyEngine Resolve(MainConst.NginxEngineMode mode) => mode switch
    {
        MainConst.NginxEngineMode.ExternalMode => ProxyEngine.External,
        MainConst.NginxEngineMode.BuiltinMode => ProxyEngine.Builtin,
        _ => Find() is null ? ProxyEngine.Builtin : ProxyEngine.External
    };

    internal static string? Find(bool coproxy = false)
    {
        lock (CacheLock)
        {
            if (CacheResolved.Contains(coproxy))
                return PathCache[coproxy];

            PathCache[coproxy] = Search(coproxy);
            CacheResolved.Add(coproxy);

            return PathCache[coproxy];
        }
    }

    // 该路径是否就是应用自带的 nginx（coproxy 场景下自带的文件名是 Cealing-Conginx）。
    internal static bool IsBundled(string nginxPath, bool coproxy = false)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(nginxPath),
                Path.GetFullPath(coproxy ? MainConst.ConginxPath : MainConst.NginxPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // 状态轮询每 100ms 调一次，这里必须限流，否则会反复重扫路径并重跑 nginx -V。
    internal static void Refresh()
    {
        lock (CacheLock)
        {
            DateTime now = DateTime.UtcNow;

            if (now - LastRefresh < RefreshInterval)
                return;

            LastRefresh = now;
            PathCache.Clear();
            CacheResolved.Clear();
        }
    }

    // 暂存副本会把可执行文件挪到另一个目录（见 BinaryStager），缓存里指向旧目录的路径必须立刻作废，
    // 否则写进配置的 NginxBinaryPath 还是 root 执行不了的 bundle 路径。
    // 这里刻意不复用 Refresh：它有 5 秒限流，而目录换了之后晚一秒都是错路径。
    internal static void Invalidate()
    {
        lock (CacheLock)
        {
            PathCache.Clear();
            CacheResolved.Clear();
            CompatibleCache.Clear();
        }
    }

    private static string? Search(bool coproxy)
    {
        foreach (string candidate in Candidates(coproxy))
            if (File.Exists(candidate) && IsCompatible(candidate))
                return candidate;

        return null;
    }

    // 系统安装的 nginx 优先，应用自带的 nginx 作为兜底。
    private static IEnumerable<string> Candidates(bool coproxy)
    {
        string fileName = OperatingSystem.IsWindows() ? "nginx.exe" : "nginx";

        foreach (string pathDir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(pathDir))
                yield return Path.Combine(pathDir.Trim(), fileName);

        foreach (string commonPath in CommonPaths)
            yield return commonPath;

        yield return coproxy ? MainConst.ConginxPath : MainConst.NginxPath;
    }

    private static bool IsCompatible(string nginxPath)
    {
        // 兼容性只取决于二进制本身，用 路径+大小+修改时间 做键缓存，避免重复拉起 nginx -V。
        string cacheKey = BuildCacheKey(nginxPath);

        lock (CacheLock)
            if (CompatibleCache.TryGetValue(cacheKey, out bool cached))
                return cached;

        bool compatible = ProbeCompatibility(nginxPath);

        lock (CacheLock)
            CompatibleCache[cacheKey] = compatible;

        return compatible;
    }

    private static string BuildCacheKey(string nginxPath)
    {
        try
        {
            FileInfo info = new(nginxPath);
            return $"{nginxPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return nginxPath; }
    }

    private static bool ProbeCompatibility(string nginxPath)
    {
        string probe = Probe(nginxPath);
        Match versionMatch = NginxVersionRegex().Match(probe);

        if (!versionMatch.Success)
            return false;

        Version version = new(int.Parse(versionMatch.Groups[1].Value), int.Parse(versionMatch.Groups[2].Value), int.Parse(versionMatch.Groups[3].Value));

        if (version < MinVersion)
            return false;

        return !probe.Contains("--without-http_ssl_module", StringComparison.Ordinal) &&
               !probe.Contains("--without-http_proxy_module", StringComparison.Ordinal);
    }

    private static string Probe(string nginxPath)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(nginxPath, "-V")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            Task<string> output = Task.Run(() => process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());

            if (!process.WaitForExit(ProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }

            return output.GetAwaiter().GetResult();
        }
        catch { return string.Empty; }
    }

    [GeneratedRegex(@"nginx/(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex NginxVersionRegex();
}
