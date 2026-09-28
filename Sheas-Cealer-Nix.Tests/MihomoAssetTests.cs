using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// mihomo 的发布资产按 CPU 指令集和 Go 版本切了好几个变体
/// （mihomo-windows-amd64-v3-v1.19.31.zip / -compatible- / -go124-）。
/// 只要前缀匹配就会随机抓到其中一个，在老 CPU 上就是「启动即崩溃」，
/// 而 Windows 上还会因为 os 判定漏掉 windows 而抓到 Linux 的 ELF。
/// </summary>
public class MihomoAssetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mihomo-asset-" + Guid.NewGuid().ToString("N")[..8]);

    // 取自 GitHub API 真实响应（MetaCubeX/mihomo v1.19.31）的骨架，顺序故意打乱，
    // 让「拿第一个前缀匹配」的实现必然选中错误变体。
    private const string ReleaseJson = """
    {
      "tag_name": "v1.19.31",
      "assets": [
        {"name":"mihomo-windows-amd64-compatible-v1.19.31.zip","browser_download_url":"https://dl/u-compatible"},
        {"name":"mihomo-windows-amd64-v3-v1.19.31.zip","browser_download_url":"https://dl/u-v3"},
        {"name":"mihomo-windows-amd64-v1-go120-v1.19.31.zip","browser_download_url":"https://dl/u-go120"},
        {"name":"mihomo-windows-amd64-v1.19.31.zip","browser_download_url":"https://dl/u-plain"},
        {"name":"mihomo-linux-amd64-compatible-v1.19.31.gz","browser_download_url":"https://dl/l-compatible"},
        {"name":"mihomo-linux-amd64-v1.19.31.gz","browser_download_url":"https://dl/l-plain"},
        {"name":"mihomo-darwin-arm64-v1.19.31.zip","browser_download_url":"https://dl/d-arm64"}
      ]
    }
    """;

    [Fact]
    public void WindowsPicksPlainZipNotInstructionSetVariants()
    {
        Assert.Equal("https://dl/u-plain", MihomoDownloader.SelectAssetUrl(ReleaseJson, "windows", "amd64", "v1.19.31"));
    }

    [Fact]
    public void LinuxPicksPlainGzNotCompatible()
    {
        Assert.Equal("https://dl/l-plain", MihomoDownloader.SelectAssetUrl(ReleaseJson, "linux", "amd64", "v1.19.31"));
    }

    [Fact]
    public void DarwinArm64PicksItsOwnAsset()
    {
        Assert.Equal("https://dl/d-arm64", MihomoDownloader.SelectAssetUrl(ReleaseJson, "darwin", "arm64", "v1.19.31"));
    }

    // 抓不到精确同名资产时必须失败，而不是退化成 -compatible / -v3 这类按指令集编译的变体。
    [Fact]
    public void MissingExactAssetYieldsEmptyInsteadOfAVariant()
    {
        Assert.Equal(string.Empty, MihomoDownloader.SelectAssetUrl(ReleaseJson, "windows", "386", "v1.19.31"));
    }

    [Fact]
    public void GarbageJsonYieldsEmpty()
    {
        Assert.Equal(string.Empty, MihomoDownloader.SelectAssetUrl("not json", "linux", "amd64", "v1.19.31"));
    }

    // Windows 解压出来的是 mihomo.exe，以前的名字比对只认裸 "mihomo"，
    // 于是下载成功却找不到二进制，用户看到的仍然是「mihomo 缺失」。
    [Fact]
    public void FindBinaryAcceptsWindowsExecutable()
    {
        Directory.CreateDirectory(_dir);
        string exe = Path.Combine(_dir, "mihomo.exe");
        File.WriteAllText(exe, "x");

        Assert.Equal(exe, MihomoDownloader.FindBinary(_dir));
    }

    [Fact]
    public void FindBinarySkipsArchivesAndMetadata()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mihomo-linux-amd64-v1.19.31.gz"), "x");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "x");
        string binary = Path.Combine(_dir, "mihomo");
        File.WriteAllText(binary, "x");

        Assert.Equal(binary, MihomoDownloader.FindBinary(_dir));
    }

    // 真实资产解压出来的文件名是 mihomo-windows-amd64.exe，既不是裸 mihomo 也不是 mihomo.exe。
    [Fact]
    public void FindBinaryAcceptsRealWindowsAssetFileName()
    {
        Directory.CreateDirectory(_dir);
        string exe = Path.Combine(_dir, "mihomo-windows-amd64.exe");
        File.WriteAllText(exe, "x");

        Assert.Equal(exe, MihomoDownloader.FindBinary(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
