using System;
using System.IO;
using System.Text;

namespace Cealing_Agent;

internal static class AgentLog
{
    // 会话内日志只增不减，跑久了会吃掉几十上百 MB；到上限丢掉旧的一半。
    internal const long MaxLogBytes = 16 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    internal static void Init(string path)
    {
        _path = path;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"[agent] started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    internal static void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            Console.Write(line);

            if (_path is null)
                return;

            try
            {
                TrimIfNeeded(_path, MaxLogBytes);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    // 引擎侧（FileLogger 等）也写同一个文件；统一从这里走才能共享截断检查和锁，
    // 否则两条写路径交错，截半时会把对方刚写的行切在中间。
    internal static void Raw(string line)
    {
        lock (Gate)
        {
            if (_path is null)
                return;

            try
            {
                TrimIfNeeded(_path, MaxLogBytes);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static void Info(string message) => Write("info", message);
    internal static void Warn(string message) => Write("warn", message);
    internal static void Error(string message) => Write("error", message);

    internal static void TrimIfNeeded(string path, long maxBytes)
    {
        if (!File.Exists(path))
            return;

        long length = new FileInfo(path).Length;

        if (length <= maxBytes)
            return;

        byte[] keepBytes;

        using (FileStream read = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read.Seek(length / 2, SeekOrigin.Begin);

            using MemoryStream rest = new();
            read.CopyTo(rest);
            keepBytes = rest.ToArray();
        }

        // 从半个文件处截起多半落在半行中间，丢到下一个换行为止
        int start = 0;

        while (start < keepBytes.Length && keepBytes[start] != (byte)'\n')
            start++;

        if (start < keepBytes.Length)
            start++;

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"[agent] log trimmed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, older half discarded{Environment.NewLine}"));

        if (start < keepBytes.Length)
        {
            // File 没有 AppendAllBytes，只能自己开一个 Append 流的句柄。
            // FileShare.ReadWrite | Delete：GUI 的日志窗口正在 tail 这个文件，截断不能把它挡在外面。
            using FileStream append = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            append.Write(keepBytes, start, keepBytes.Length - start);
        }
    }
}
