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

        int start = content.IndexOf(StartMarker, StringComparison.Ordinal);

        if (start == -1)
            return;

        int end = content.LastIndexOf(EndMarker, StringComparison.Ordinal);

        if (end == -1)
            return;

        File.SetAttributes(hostsPath, File.GetAttributes(hostsPath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(hostsPath, content.Remove(start, end - start + EndMarker.Length), Utf8NoBom);
    }
}
