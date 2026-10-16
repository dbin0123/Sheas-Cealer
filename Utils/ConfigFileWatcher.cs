using System;
using System.IO;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 把「配置文件被改」这件事订全：原地写入和「临时文件改名覆盖」都要能收到。
/// </summary>
internal static class ConfigFileWatcher
{
    internal static void Subscribe(FileSystemWatcher watcher, Action<FileSystemEventArgs> handler)
    {
        // 没有 FileName，Windows 根本不会把改名事件上报上来，订 Renamed 也收不到。
        watcher.NotifyFilter |= NotifyFilters.FileName;

        watcher.Changed += (_, e) => handler(e);
        watcher.Created += (_, e) => handler(e);
        watcher.Renamed += (_, e) => handler(e);
    }
}
