using Cealing_Core;
using Sheas_Cealer_Nix.Utils;
using System;
using System.Collections.Generic;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class ProxyConfigBuilderTests
{
    private static ProxyConfig NewConfig() => new();

    [Fact]
    public void BuildConfigStripsRegexMarkersFromServerName()
    {
        SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?> rules = new()
        {
            ["U"] = [([("*.google.com", "")], "g.cn", "1.2.3.4")]
        };

        ProxyController controller = new(() => rules, () => false, () => ProxyEngineKind.Builtin, () => null);

        ProxyConfig config = controller.BuildConfig(coproxy: false, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880, nginxConfText: null, mihomoConfText: null);

        ProxyRule rule = Assert.Single(config.Rules);

        // 不能带 '~'/'|'：'|' 结尾会在正则里形成空分支，导致规则匹配任意域名。
        Assert.Equal("^.*\\.google\\.com$", rule.ServerName);
    }

    [Fact]
    public void BuildServerNameJoinsPairsWithPipe()
    {
        string serverName = ProxyController.BuildServerName([("a.com", ""), ("b.com", "")], out int count);

        Assert.Equal(2, count);
        Assert.StartsWith("~", serverName);
        Assert.EndsWith("|", serverName);
        Assert.Contains("^a\\.com$", serverName);
        Assert.Contains("^b\\.com$", serverName);
    }

    [Fact]
    public void BuildServerNameAddsNegativeLookaheadForExclude()
    {
        string serverName = ProxyController.BuildServerName([("*.pixiv.net", "i.pximg.net")], out _);

        Assert.Contains("(?!i\\.pximg\\.net)", serverName);
        Assert.Contains("\\.net$", serverName);
    }

    [Fact]
    public void BuildServerNameSkipsHashPrefixedDomains()
    {
        string serverName = ProxyController.BuildServerName([("#google.com", ""), ("ok.com", "")], out int count);

        Assert.Equal(1, count);
        Assert.DoesNotContain("google", serverName);
    }

    [Fact]
    public void BuildServerNameReturnsEmptyWhenNothingUsable()
    {
        Assert.Equal(string.Empty, ProxyController.BuildServerName([("#x", "")], out int count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void DollarPrefixIsBrowserOnlyAndStrippedFromRegex()
    {
        string serverName = ProxyController.BuildServerName([("$foo.com", "")], out _);

        Assert.Contains("^foo\\.com$", serverName);
        Assert.DoesNotContain("$foo", serverName);
    }

    [Fact]
    public void AddCertSanPlainDomain()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "cdn.jsdelivr.net");

        ProxyCertSan san = Assert.Single(config.CertSans);
        Assert.Equal("cdn.jsdelivr.net", san.Domain);
        Assert.False(san.Wildcard);
    }

    [Fact]
    public void AddCertSanWildcardDoesNotAddApex()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "*.fanbox.cc");

        ProxyCertSan san = Assert.Single(config.CertSans);
        Assert.Equal("fanbox.cc", san.Domain);
        Assert.True(san.Wildcard);
    }

    [Fact]
    public void AddCertSanBareWildcardAddsBoth()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "*example.org");

        Assert.Equal(2, config.CertSans.Count);
        Assert.True(config.CertSans[0].Wildcard);
        Assert.False(config.CertSans[1].Wildcard);
    }

    [Fact]
    public void AddCertSanSkipsUnusable()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "#off.com");
        ProxyController.AddCertSan(config, "  ");
        ProxyController.AddCertSan(config, "a*b.com");

        Assert.Empty(config.CertSans);
    }
}
