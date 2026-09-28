using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 自动下载 mihomo 以支持 coproxy 模式（TUN DNS 劫持，支持通配符匹配）。
/// 通过 curl --resolve 绕过 hosts 文件中的 github.com 重定向。
/// </summary>
internal static class MihomoDownloader
{
    private static readonly string[] GitHubApiHosts = ["api.github.com", "github.com", "codeload.github.com", "objects.githubusercontent.com"];
    private static readonly string LatestReleaseUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";
    // 2026-09-28 实测：ghproxy.com / mirror.ghproxy.com 已经连不上（curl 000），
    // 直连 GitHub  release 下载只有 ~25KB/s（21MB 资产 5 分钟都跑不完），所以镜像是主路径。
    private static readonly string[] MirrorPrefixes = ["https://gh-proxy.org/", "https://ghfast.top/", "https://ghproxy.net/", "https://gh-proxy.com/"];

    /// <summary>
    /// 确保 mihomo 可用。如果不存在则自动下载。
    /// 返回 mihomo 二进制路径，失败返回 null。
    /// </summary>
    public static async Task<string?> EnsureAsync(string targetPath, CancellationToken ct = default)
    {
        if (File.Exists(targetPath))
            return targetPath;

        try
        {
            string assetUrl = await GetAssetUrlAsync(ct);
            if (assetUrl.Length == 0)
                return null;

            string tempDir = Path.Combine(Path.GetTempPath(), "mihomo-download-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            string archivePath = Path.Combine(tempDir,
                assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "mihomo.zip" : "mihomo.gz");

            try
            {
                bool downloaded = await DownloadWithCurlAsync(assetUrl, archivePath, ct);
                if (!downloaded)
                    return null;

                if (!ExtractArchive(archivePath, tempDir))
                    return null;

                string binaryPath = FindBinary(tempDir);
                if (binaryPath.Length == 0)
                    return null;

                return CopyBinaryToTarget(binaryPath, targetPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> GetAssetUrlAsync(CancellationToken ct)
    {
        // 尝试直接通过 curl --resolve 调用 GitHub API
        string json = await CurlAsync(LatestReleaseUrl, ct);
        if (json.Length > 0)
            return ExtractAssetUrl(json);

        // 尝试通过镜像
        foreach (string mirror in MirrorPrefixes)
        {
            json = await CurlAsync(mirror + LatestReleaseUrl, ct);
            if (json.Length > 0)
                return ExtractAssetUrl(json);
        }

        return string.Empty;
    }

    private static string ExtractAssetUrl(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            string os = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsMacOS() ? "darwin"
                : "linux";
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                _ => "amd64"
            };

            return SelectAssetUrl(json, os, arch, doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 只认「mihomo-{os}-{arch}-{tag}」这一支精确同名资产。
    /// 前缀匹配会抓到 -compatible / -v3（按 CPU 指令集编译）/ -go120（按 Go 版本）这些变体，
    /// 在老 CPU 上就是启动即崩；而 windows 以外还会抓到 .deb / .pkg.tar.zst 这种解不开的东西。
    /// 宁缺毋滥：抓不到就返回空，让 GUI 明确提示「mihomo 缺失」，不要塞一个跑不起来的东西进去。
    /// </summary>
    internal static string SelectAssetUrl(string json, string os, string arch, string tag)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string stem = $"mihomo-{os}-{arch}-{tag}";

            foreach (string extension in new[] { ".zip", ".gz" })
            {
                foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    if (!string.Equals(asset.GetProperty("name").GetString(), stem + extension, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> DownloadWithCurlAsync(string url, string outputPath, CancellationToken ct)
    {
        // 尝试直接下载（通过 --resolve 绕过 hosts）
        if (await CurlDownloadAsync(url, outputPath, ct))
            return true;

        // 尝试通过镜像
        foreach (string mirror in MirrorPrefixes)
        {
            string mirrorUrl = mirror + url;
            if (await CurlDownloadAsync(mirrorUrl, outputPath, ct))
                return true;
        }

        return false;
    }

    private static async Task<bool> CurlDownloadAsync(string url, string outputPath, CancellationToken ct)
    {
        try
        {
            string resolveArgs = BuildResolveArgs(url);
            ProcessStartInfo info = new()
            {
                FileName = "curl",
                Arguments = $"--disable --silent --show-error --location --fail --retry 3 --connect-timeout 15 {resolveArgs} -o \"{outputPath}\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(info);
            if (process is null)
                return false;

            await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> CurlAsync(string url, CancellationToken ct)
    {
        try
        {
            string resolveArgs = BuildResolveArgs(url);
            ProcessStartInfo info = new()
            {
                FileName = "curl",
                Arguments = $"--disable --silent --show-error --location --fail --connect-timeout 15 {resolveArgs} \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(info);
            if (process is null)
                return string.Empty;

            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildResolveArgs(string url)
    {
        if (!url.StartsWith("https://github.com", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://codeload.github.com", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        ProcessStartInfo nslookupInfo = new()
        {
            FileName = "nslookup",
            Arguments = GetHostFromUrl(url),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process nslookup = Process.Start(nslookupInfo);
        if (nslookup is null)
            return string.Empty;

        string output = nslookup.StandardOutput.ReadToEnd();
        nslookup.WaitForExit(3000);

        if (nslookup.ExitCode != 0)
            return string.Empty;

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Address:") && trimmed.Contains("."))
            {
                string[] parts = trimmed.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string ip = parts[parts.Length - 1].Trim();
                    if (ip.Contains(".") && !ip.StartsWith("127."))
                        return $"--resolve {GetHostFromUrl(url)}:443:{ip}";
                }
            }
        }

        return string.Empty;
    }

    private static string GetHostFromUrl(string url)
    {
        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal) + 3;
        int hostEnd = url.IndexOf('/', schemeEnd);
        if (hostEnd < 0)
            hostEnd = url.Length;
        return url.Substring(schemeEnd, hostEnd - schemeEnd);
    }

    /// <summary>
    /// 用 BCL 解压，不外部调 unzip/tar：Windows 压根没有 unzip，而 mihomo 的 linux/darwin 资产
    /// 是裸 gzip 的二进制（不是 tar 包），以前把它当 .zip 交给 unzip 解，必然失败。
    /// </summary>
    private static bool ExtractArchive(string archivePath, string destDir)
    {
        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destDir);
                return true;
            }

            using GZipStream gzip = new(File.OpenRead(archivePath), CompressionMode.Decompress);
            using FileStream output = File.Create(Path.Combine(destDir, Path.GetFileNameWithoutExtension(archivePath)));
            gzip.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? CopyBinaryToTarget(string binaryPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(binaryPath, targetPath, overwrite: true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            File.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return targetPath;
    }

    internal static string FindBinary(string dir)
    {
        foreach (string file in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("mihomo", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
                (name.StartsWith("mihomo-") && !name.EndsWith(".zip") && !name.EndsWith(".gz") && !name.EndsWith(".md") && !name.EndsWith(".json") && !name.EndsWith(".yaml") && !name.EndsWith(".yml")))
            {
                return file;
            }
        }

        foreach (string subDir in Directory.GetDirectories(dir))
        {
            string found = FindBinary(subDir);
            if (found.Length > 0)
                return found;
        }

        return string.Empty;
    }
}
