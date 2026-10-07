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
}
