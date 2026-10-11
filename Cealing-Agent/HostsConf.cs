using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cealing_Agent;

internal static class HostsConf
{
    internal const string StartMarker = "# Cealing Nginx Start";
    internal const string EndMarker = "# Cealing Nginx End";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 通配符域名没法用 hosts 表达（hosts 不支持 *.example.com），
    // 只能退而求其次给 www.<domain> 指一条，和 GUI 侧原来的行为保持一致。
    internal static string BuildBlock(IEnumerable<(string domain, bool wildcard)> sans)
    {
        StringBuilder builder = new();

        builder.Append(StartMarker).Append(Environment.NewLine);

        foreach ((string domain, bool wildcard) in sans)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain.Contains('*'))
                continue;

            if (wildcard)
            {
                builder.Append("127.0.0.1 www.").Append(domain).Append(Environment.NewLine);

                continue;
            }

            builder.Append("127.0.0.1 ").Append(domain).Append(Environment.NewLine);
        }

        builder.Append(EndMarker).Append(Environment.NewLine);

        return builder.ToString();
    }

    internal static void Append(string hostsPath, string block)
    {
        Remove(hostsPath);

        File.SetAttributes(hostsPath, File.GetAttributes(hostsPath) & ~FileAttributes.ReadOnly);
        File.AppendAllText(hostsPath, block, Utf8NoBom);
    }

    internal static void Remove(string hostsPath)
    {
        if (!File.Exists(hostsPath))
            return;

        string content = File.ReadAllText(hostsPath);
        bool changed = false;
        int start;

        // 一轮一轮清：崩溃现场可以留下「一个完整块 + 一个只写了起始标记的半块」，
        // 只删第一个匹配到的块会剩一段伪造记录在文件里。
        while ((start = content.IndexOf(StartMarker, StringComparison.Ordinal)) != -1)
        {
            int end = content.IndexOf(EndMarker, start + StartMarker.Length, StringComparison.Ordinal);

            // 只有起始标记、没有结束标记 = 上一任 agent 写到一半被杀。块是我们追加在文件末尾的，
            // 所以标记之后到文件结尾全是我们的内容，必须一并清掉；
            // 早先这里直接 return，卸载就永久留一段伪造的 hosts（用户表现为对应域名上不了网）。
            int length = end == -1 ? content.Length - start : end + EndMarker.Length - start;

            content = content.Remove(start, length);
            changed = true;
        }

        if (!changed)
            return;

        File.SetAttributes(hostsPath, File.GetAttributes(hostsPath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(hostsPath, content, Utf8NoBom);
    }
}
