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

        ProxyController controller = new(() => rules, () => false, () => ProxyEngineKind.Builtin, () => null, () => string.Empty);

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

    [Fact]
    public void SameDomainFromTwoRuleFilesBecomesOneSan()
    {
        // 自带规则 B 和上游规则 U 完全可能列出同一个域名。SAN 会同时进证书和 hosts，
        // 不去重的话证书里就多一条同名 SAN、hosts 里多一行一样的记录。
        ProxyController controller = new(
            () => new SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?>
            {
                ["U"] = [([("google.com", "")], "g.cn", "1.2.3.4")],
                ["B"] = [([("google.com", "")], "g.cn", "1.2.3.4")]
            },
            () => false,
            () => ProxyEngineKind.Builtin,
            () => null,
            () => string.Empty);

        ProxyConfig config = controller.BuildConfig(coproxy: false, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880, nginxConfText: null, mihomoConfText: null);

        ProxyCertSan san = Assert.Single(config.CertSans);

        Assert.Equal("google.com", san.Domain);
        Assert.False(san.Wildcard);
    }

    [Fact]
    public void WildcardAndApexOfSameDomainStaySeparate()
    {
        // 去重只认「同域名同通配形态」：*.google.com 和 google.com 是两条不同的 SAN，
        // 按域名去掉重会把通配那条吞掉，子域名反而拿不到证书。
        ProxyController controller = new(
            () => new SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?>
            {
                ["L"] = [([("*google.com", "")], "g.cn", "1.2.3.4")],
                ["B"] = [([("google.com", "")], "g.cn", "1.2.3.4")]
            },
            () => false,
            () => ProxyEngineKind.Builtin,
            () => null,
            () => string.Empty);

        ProxyConfig config = controller.BuildConfig(coproxy: false, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880, nginxConfText: null, mihomoConfText: null);

        // 顺序按规则文件名排（B 在 L 前面），所以这里只问「两条在不在」，不问先后。
        Assert.Equal(2, config.CertSans.Count);
        Assert.Contains(config.CertSans, san => san.Domain == "google.com" && san.Wildcard);
        Assert.Contains(config.CertSans, san => san.Domain == "google.com" && !san.Wildcard);
    }

    [Fact]
    public void ConfiguredJavaTrustPathsSplitOnSemicolon()
    {
        // 设置页里是一整行文本，agent 只吃干净的条目。
        ProxyController controller = new(
            () => new SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?>(),
            () => false,
            () => ProxyEngineKind.Builtin,
            () => null,
            () => " C:\\Users\\me\\.jdks\\jbr-21 ; ;;  \n");

        ProxyConfig config = controller.BuildConfig(coproxy: false, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880, nginxConfText: null, mihomoConfText: null);

        Assert.Equal(["C:\\Users\\me\\.jdks\\jbr-21"], config.JavaTrustPaths);
    }
}
