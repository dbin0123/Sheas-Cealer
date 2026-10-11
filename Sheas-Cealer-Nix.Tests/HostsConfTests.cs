using Cealing_Agent;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class HostsConfTests : IDisposable
{
    private readonly string TempHosts = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public HostsConfTests() => File.WriteAllText(TempHosts, "127.0.0.1 localhost\n");

    public void Dispose() => File.Delete(TempHosts);

    [Fact]
    public void BuildBlockWrapsWithMarkers()
    {
        string block = HostsConf.BuildBlock([("pixiv.net", false)]);

        Assert.StartsWith(HostsConf.StartMarker + Environment.NewLine, block);
        Assert.Contains("127.0.0.1 pixiv.net", block);
        Assert.EndsWith(HostsConf.EndMarker + Environment.NewLine, block);
    }

    [Fact]
    public void BuildBlockMapsWildcardToWwwOnly()
    {
        string block = HostsConf.BuildBlock([("fanbox.cc", true)]);

        Assert.Contains("127.0.0.1 www.fanbox.cc", block);
        Assert.DoesNotContain("\n127.0.0.1 fanbox.cc", block);
    }

    [Fact]
    public void BuildBlockSkipsUnusableDomains()
    {
        string block = HostsConf.BuildBlock([("", false), ("  ", false), ("a*b.example", false)]);

        Assert.DoesNotContain("127.0.0.1", block);
    }

    [Fact]
    public void RemoveStripsOnlyOurBlock()
    {
        File.WriteAllText(TempHosts, "127.0.0.1 localhost\n" + HostsConf.BuildBlock([("pixiv.net", false)]) + "::1 ip6-localhost\n");

        HostsConf.Remove(TempHosts);

        string content = File.ReadAllText(TempHosts);
        Assert.DoesNotContain(HostsConf.StartMarker, content);
        Assert.Contains("127.0.0.1 localhost", content);
        Assert.Contains("::1 ip6-localhost", content);
    }

    [Fact]
    public void RemoveIsNoOpWhenNoBlock()
    {
        HostsConf.Remove(TempHosts);

        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(TempHosts));
    }

    // 崩溃现场可以留下「一个完整块 + 一个只有起始标记的半块」，也可能留下好几个块
    // （旧版按标记追加、新版的 Remove 只清第一个匹配到的）。卸载必须一次全清掉。
    [Fact]
    public void RemoveStripsEveryBlockNotJustTheFirst()
    {
        File.WriteAllText(TempHosts,
            "127.0.0.1 localhost\n"
            + HostsConf.BuildBlock([("a.net", false)])
            + "middle\n"
            + HostsConf.BuildBlock([("b.net", false)]));

        HostsConf.Remove(TempHosts);

        string content = File.ReadAllText(TempHosts);

        Assert.DoesNotContain(HostsConf.StartMarker, content);
        Assert.DoesNotContain("127.0.0.1 a.net", content);
        Assert.DoesNotContain("127.0.0.1 b.net", content);
        Assert.Contains("middle", content);
    }

    [Fact]
    public void RemoveClearsHalfWrittenBlockToEof()
    {
        // 只有起始标记、没有结束标记 = agent 写到一半被 SIGKILL。伪造条目全在标记之后。
        File.WriteAllText(TempHosts,
            "127.0.0.1 localhost\n"
            + HostsConf.StartMarker + "\n127.0.0.1 a.net\n127.0.0.1 www.b.net\n");

        HostsConf.Remove(TempHosts);

        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(TempHosts));
    }

    [Fact]
    public void RemoveClearsHalfWrittenBlockAfterACompleteOne()
    {
        File.WriteAllText(TempHosts,
            HostsConf.BuildBlock([("a.net", false)])
            + HostsConf.StartMarker + "\n127.0.0.1 b.net\n");

        HostsConf.Remove(TempHosts);

        Assert.DoesNotContain(HostsConf.StartMarker, File.ReadAllText(TempHosts));
    }

    [Fact]
    public void AppendIsIdempotent()
    {
        HostsConf.Append(TempHosts, HostsConf.BuildBlock([("pixiv.net", false)]));
        HostsConf.Append(TempHosts, HostsConf.BuildBlock([("fanbox.cc", false)]));

        string content = File.ReadAllText(TempHosts);
        Assert.Equal(1, CountOccurrences(content, HostsConf.StartMarker));
        Assert.DoesNotContain("pixiv.net", content);
        Assert.Contains("fanbox.cc", content);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
