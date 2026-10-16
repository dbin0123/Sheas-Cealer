using Sheas_Cealer_Nix.Utils;
using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public sealed class ConfigFileWatcherTests : IDisposable
{
    private const string Filter = "Cealing-Host-*.json";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cealing-watch-" + Path.GetRandomFileName());

    public ConfigFileWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    // vim / sed -i / 多数命令行工具存盘的做法是「写临时文件再改名覆盖目标」，
    // 目录里看到的是一次 Rename，而不是 LastWrite。只订 Changed 的话规则改了却不生效，
    // 界面上一声不响 —— 用户只能以为程序坏了。
    [Fact]
    public void ReplaceByRenameRaisesHandler()
    {
        string target = Path.Combine(_dir, "Cealing-Host-L.json");
        File.WriteAllText(target, "[]");

        using ManualResetEventSlim raised = new(false);
        using FileSystemWatcher watcher = Watch(NotifyFilters.LastWrite, _ => raised.Set());

        string staging = Path.Combine(_dir, "staging.json");
        File.WriteAllText(staging, """[["google.com.hk"],"g.cn","183.56.143.147"]""");
        File.Move(staging, target, true);

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "rename-over-target never reached the handler");
    }

    // 记事本这类原地写入是老路径，必须继续有效。
    [Fact]
    public void WriteInPlaceRaisesHandler()
    {
        string target = Path.Combine(_dir, "Cealing-Host-L.json");
        File.WriteAllText(target, "[]");

        using ManualResetEventSlim raised = new(false);
        using FileSystemWatcher watcher = Watch(NotifyFilters.LastWrite, _ => raised.Set());

        File.WriteAllText(target, "[[\"translate.googleapis.com\"],\"g.cn\",\"183.56.143.147\"]");

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "in-place write never reached the handler");
    }

    private FileSystemWatcher Watch(NotifyFilters notifyFilter, Action<FileSystemEventArgs> handler)
    {
        FileSystemWatcher watcher = new(_dir, Filter) { NotifyFilter = notifyFilter };
        ConfigFileWatcher.Subscribe(watcher, handler);
        watcher.EnableRaisingEvents = true;

        return watcher;
    }
}
