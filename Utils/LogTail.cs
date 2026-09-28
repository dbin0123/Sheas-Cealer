using System;
using System.IO;
using System.Text;

namespace Sheas_Cealer_Nix.Utils;

// agent 日志（cealing-agent.log）的尾部读取：日志可能被 agent 频繁追加、重启时整体重写，
// 且随时长无上限增长，所以 GUI 侧只取尾部并以增量方式跟随。
internal static class LogTail
{
    internal const long MaxTailBytes = 512 * 1024;

    internal static long GetCurrentLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    internal static string ReadTail(string path, long maxBytes = MaxTailBytes)
    {
        if (!File.Exists(path))
            return string.Empty;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long windowStart = Math.Max(0, stream.Length - maxBytes);

            stream.Seek(windowStart, SeekOrigin.Begin);

            using MemoryStream buffered = new();
            stream.CopyTo(buffered);

            return TrimPartialLeadingLine(buffered.ToArray(), windowStart > 0);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    // 返回自上次读取以来新增的文本；无变化或文件不存在返回 null。
    // 文件变短说明 agent 重启重写过，返回最新尾部；一次增长超出 maxBytes 时直接回退到尾部读取。
    internal static string? ReadIncrement(string path, ref long lastLength, long maxBytes = MaxTailBytes)
    {
        long currentLength;

        try
        {
            if (!File.Exists(path))
                return null;

            currentLength = new FileInfo(path).Length;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (currentLength == lastLength)
            return null;

        if (currentLength < lastLength || lastLength <= 0 || currentLength - lastLength > maxBytes)
        {
            string tail = ReadTail(path, maxBytes);
            lastLength = currentLength;

            return tail;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(lastLength, SeekOrigin.Begin);

            using MemoryStream buffered = new();
            stream.CopyTo(buffered);

            lastLength = currentLength;

            return Encoding.UTF8.GetString(buffered.ToArray());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // 从文件中部截起时，窗口开头大概率落在某个多字节字符或半行日志中间；
    // 丢掉窗口开头到第一个换行为止的内容，代价最多一行。
    private static string TrimPartialLeadingLine(byte[] bytes, bool truncated)
    {
        int start = 0;

        if (truncated)
            while (start < bytes.Length && bytes[start] != (byte)'\n')
                start++;

        if (start < bytes.Length && bytes[start] == (byte)'\n')
            start++;

        if (start >= bytes.Length)
            return string.Empty;

        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
