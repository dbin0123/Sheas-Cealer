using Sheas_Cealer_Nix.Utils;
using System.Collections.Generic;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class MihomoTunRoutesTests
{
    [Fact]
    public void UpstreamIpsBecomeHostCidrs()
    {
        List<string> excluded = MihomoTunRoutes.BuildRouteExcludeAddresses(new List<string?> { "183.56.143.147", "151.101.78.208" });

        Assert.Equal(new[] { "183.56.143.147/32", "151.101.78.208/32" }, excluded);
    }

    // 规则里大量上游 IP 就是 127.0.0.1，排除环回没有意义；空值和域名也不该变成条目。
    [Fact]
    public void LoopbackBlankAndNonIpEntriesAreIgnored()
    {
        List<string> excluded = MihomoTunRoutes.BuildRouteExcludeAddresses(
            new List<string?> { "127.0.0.1", "::1", string.Empty, null, "  ", "example.com" });

        Assert.Empty(excluded);
    }

    [Fact]
    public void Ipv6UsesSlash128AndDuplicatesCollapse()
    {
        List<string> excluded = MihomoTunRoutes.BuildRouteExcludeAddresses(
            new List<string?> { "2001:db8::1", "2001:db8::1", " 89.149.221.236 " });

        Assert.Equal(new[] { "2001:db8::1/128", "89.149.221.236/32" }, excluded);
    }

    // 回归：上游 Cealing-Host 的 IPv6 源站一律带方括号（claude.ai / netflix / spotify / yahoo 那几条），
    // 因为外部 nginx 的 proxy_pass 只吃这个形态。IPAddress.TryParse 不认方括号，
    // 于是这些源站从来没被排除出 TUN，引擎回源被 auto-route 塞回隧道、再被 hosts 映射回 127.0.0.1。
    [Fact]
    public void BracketedIpv6OriginsAreExcludedToo()
    {
        List<string> excluded = MihomoTunRoutes.BuildRouteExcludeAddresses(
            new List<string?> { "[2a01:4f8:c2c:123f:64:5:2cf2:0da1]", " [2001:db8::1] " });

        // 出来的是规范化写法（每组前导零去掉），mihomo 的 route-exclude-address 要的就是这个。
        Assert.Equal(new[] { "2a01:4f8:c2c:123f:64:5:2cf2:da1/128", "2001:db8::1/128" }, excluded);
    }
}
