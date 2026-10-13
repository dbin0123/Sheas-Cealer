using Sheas_Cealer_Nix.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public sealed class MihomoConfTextTests : IDisposable
{
    private const string UserConf = """
        mixed-port: 7890
        proxies:
          - name: tokyo
            type: ss
            server: 1.2.3.4
        rules:
          - GEOIP,CN,DIRECT
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // 回归：config.yaml 曾在同一个 FileStream 上被 ReadToEnd 两遍。第一遍把位置推到末尾，
    // 第二遍拿到空串，而那个空串同时成了「停止后写回给用户的原文」，
    // 于是点一次「启动 mihomo」就把用户的节点和分流规则清成 0 字节。
    [Fact]
    public void ReadReturnsTheWholeFileEveryTimeItIsCalled()
    {
        File.WriteAllText(_path, UserConf);

        string first = MihomoConfText.Read(_path);
        string second = MihomoConfText.Read(_path);

        Assert.Equal(UserConf, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ReadTreatsAMissingFileAsEmpty()
    {
        Assert.Equal(string.Empty, MihomoConfText.Read(_path));
    }

    // 两个变体配置（注入 dns.nameserver 的和不注入的）从同一份文本各自解析。
    // 共用一棵对象树的话，后一次赋值会悄悄改掉前一次的结果。
    [Fact]
    public void ParseReturnsIndependentTreesForTheSameText()
    {
        Dictionary<string, object> full = MihomoConfText.Parse(UserConf);
        Dictionary<string, object> hosts = MihomoConfText.Parse(UserConf);

        Assert.NotSame(full, hosts);
        Assert.True(full.ContainsKey("proxies"));
        Assert.True(hosts.ContainsKey("proxies"));

        full["tun"] = "injected";

        Assert.False(hosts.ContainsKey("tun"));
    }

    [Fact]
    public void ParseToleratesAnEmptyFile()
    {
        Assert.Empty(MihomoConfText.Parse(string.Empty));
    }
}
