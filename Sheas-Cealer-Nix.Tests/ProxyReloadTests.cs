using Cealing_Core;
using Sheas_Cealer_Nix.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// 规则改完必须当场生效。以前只有点「启动全局伪造」那一下才会把配置写盘并交给 agent，
/// 于是热重载只改了内存：hosts、证书 SAN、引擎规则还停在旧那份上，
/// 用户只能「停止→再启动」反复开关（实测一次排错要开关四轮）。
/// </summary>
public class ProxyReloadTests
{
    private static SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?> RulesOf(
        params (string domain, string? sni, string ip)[] rules)
    {
        List<(List<(string include, string exclude)> pairs, string? sni, string ip)> list = [];

        foreach ((string domain, string? sni, string ip) in rules)
            list.Add((new List<(string include, string exclude)> { (domain, string.Empty) }, sni, ip));

        return new SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?> { ["B"] = list };
    }

    private static ProxyController NewController(
        SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?> rules,
        bool flashing = false) =>
        new(() => rules, () => flashing, () => ProxyEngineKind.Builtin, () => null, () => string.Empty);

    [Fact]
    public void ReloadKeepsStartedTransportSettingsAndReplacesDerivedOnes()
    {
        SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?> rules =
            RulesOf(("translate.googleapis.com", "g.cn", "183.56.143.147"));
        ProxyController controller = NewController(rules);

        ProxyConfig running = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 81, httpsPort: 444, mixedPort: 7881,
            nginxConfText: "OLD-NGINX", mihomoConfText: "OLD-MIHOMO");

        // 代理跑起来之后用户往规则里加了 Antigravity 的域名，并重新生成了两份引擎配置文本。
        rules["B"] = RulesOf(
            ("translate.googleapis.com", "g.cn", "183.56.143.147"),
            ("cloudcode-pa.googleapis.com", "g.cn", "183.56.143.147"))["B"]!;

        ProxyConfig reloaded = controller.RefreshRunningConfig(running, "NEW-NGINX", "NEW-MIHOMO");

        // 端口/引擎/是否 coproxy 是启动那一刻定下来的，重载动它们等于把代理换到别的端口上。
        Assert.True(reloaded.Coproxy);
        Assert.False(reloaded.WriteHosts);
        Assert.Equal(81, reloaded.HttpPort);
        Assert.Equal(444, reloaded.HttpsPort);
        Assert.Equal(7881, reloaded.MixedPort);

        Assert.Equal("NEW-NGINX", reloaded.NginxConfText);
        Assert.Equal("NEW-MIHOMO", reloaded.MihomoConfText);

        // 从规则派生的两部分必须换成新的：转发规则 + 证书 SAN（SAN 同时喂给 hosts）。
        Assert.Equal(2, reloaded.Rules.Count);
        Assert.Equal(2, reloaded.CertSans.Count);
        Assert.Contains("^cloudcode-pa\\.googleapis\\.com$", reloaded.Rules.Select(r => r.ServerName));
        Assert.Contains("cloudcode-pa.googleapis.com", reloaded.CertSans.Select(s => s.Domain));
    }

    // 删掉一条规则后，旧域名不能继续留在 SAN 里：hosts 会照旧把它指到 127.0.0.1，
    // 而引擎已经不再接手 —— 表现是「这个网站彻底打不开了」，比不生效更糟。
    [Fact]
    public void ReloadDropsSansAndRulesOfRemovedDomains()
    {
        ProxyController controller = NewController(RulesOf(
            ("translate.googleapis.com", "g.cn", "183.56.143.147"),
            ("antigravity.google", "g.cn", "183.56.143.147")));

        ProxyConfig running = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: null, mihomoConfText: "OLD-MIHOMO");

        Assert.Equal(2, running.CertSans.Count);

        controller = NewController(RulesOf(("translate.googleapis.com", "g.cn", "183.56.143.147")));
        running = controller.RefreshRunningConfig(running, null, "NEW-MIHOMO");

        ProxyRule rule = Assert.Single(running.Rules);
        ProxyCertSan san = Assert.Single(running.CertSans);

        Assert.DoesNotContain("antigravity", rule.ServerName);
        Assert.Equal("translate.googleapis.com", san.Domain);
    }

    // 代理没起来时重载必须是空操作 —— 落一份 cealing-proxy.json 到磁盘上会让下一次
    // 「启动全局伪造」之外的任何路径误以为配置已经就位。
    [Fact]
    public async Task ReloadIsANoopWhenNothingIsRunning()
    {
        ProxyController controller = NewController(RulesOf(("a.com", "g.cn", "1.2.3.4")));

        Assert.Null(await controller.ReloadRunningAsync());
    }

    // Windows 的 FileSystemWatcher 一次保存会报 2~3 个事件，而 reload 等于把引擎整个重启
    // （TUN 断一下、根证书重新导入、Java 信任库重新扫）。内容其实没变的那些事件必须被吃掉，
    // 否则「存一次断十几秒」会变成用户眼里的新 bug。
    [Fact]
    public void IdenticalRebuildCountsAsNoChange()
    {
        ProxyController controller = NewController(RulesOf(("a.com", "g.cn", "1.2.3.4"), ("b.com", "g.cn", "1.2.3.4")));

        ProxyConfig first = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO");
        ProxyConfig again = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO");

        Assert.True(ProxyController.ConfigsEqual(first, again));
    }

    // runningConfig 是「上一次真正生效的那份」，重载拿它当基准判断内容变没变。
    // 若 RefreshRunningConfig 原地改它，基准会被最新一份污染：两次相同内容的保存
    // 就再也不会被认成「没变化」，多次重启的问题会原样回来；而重载失败时基准也回不去。
    [Fact]
    public void RefreshLeavesTheAppliedConfigUntouched()
    {
        ProxyController controller = NewController(RulesOf(("a.com", "g.cn", "1.2.3.4")));

        ProxyConfig applied = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO");

        ProxyConfig candidate = controller.RefreshRunningConfig(applied, "NEW-NGINX", "NEW-MIHOMO");

        Assert.NotSame(applied, candidate);
        Assert.Equal("NGINX", applied.NginxConfText);
        Assert.Equal("MIHOMO", applied.MihomoConfText);
        Assert.False(ProxyController.ConfigsEqual(applied, candidate));
    }

    [Fact]
    public void ChangedRulesOrConfTextsCountAsChange()
    {
        ProxyController controller = NewController(RulesOf(("a.com", "g.cn", "1.2.3.4")));

        ProxyConfig first = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO");

        controller = NewController(RulesOf(("a.com", "g.cn", "1.2.3.4"), ("b.com", "g.cn", "1.2.3.4")));
        ProxyConfig addedRule = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO");

        Assert.False(ProxyController.ConfigsEqual(first, addedRule));

        // 只换 mihomo 那份文本（hosts 段变了、规则没变）也必须算变化，否则新域名照样不上线。
        ProxyConfig changedText = controller.BuildConfig(
            coproxy: true, writeHosts: false, httpPort: 80, httpsPort: 443, mixedPort: 7880,
            nginxConfText: "NGINX", mihomoConfText: "MIHOMO-CHANGED");

        Assert.False(ProxyController.ConfigsEqual(addedRule, changedText));
    }
}
