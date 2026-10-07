using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Sheas_Cealer_Nix.Utils;

// mihomo 的 auto-route 在 Linux 下会下发 `from 0.0.0.0 iif lo lookup <tun 表>`，
// 把本机**所有进程**发起的出站都交给 TUN，引擎的回源（agent 直连规则里的 IP）也不例外。
// 这条路径完全指望 mihomo 接住 TUN 里的 TCP：tun.stack 不是 gvisor 时它压根不建连接，
// 回源就一路连到超时（浏览器侧 HTTP 502，全局模式下 447 个域名全挂）。
//
// 规则里的 IP 是引擎自己的上游，本来就该绕过 TUN，所以逐个写进 route-exclude-address：
// 回源直接走物理网卡，不经过引擎这层，少一跳也少一个失败点。
// mihomo 自己的出站不受影响（它按 auto-detect-interface 绑到了物理网卡）。
internal static class MihomoTunRoutes
{
    internal static List<string> BuildRouteExcludeAddresses(IEnumerable<string?> upstreamIps)
    {
        List<string> excluded = [];

        foreach (string? raw in upstreamIps)
        {
            if (!IPAddress.TryParse(raw?.Trim(), out IPAddress? address))
                continue;

            // 环回地址不经过 TUN，排除它没有意义（规则里大量 IP 就是 127.0.0.1）。
            if (IPAddress.IsLoopback(address))
                continue;

            excluded.Add(address.AddressFamily == AddressFamily.InterNetwork
                ? $"{address}/32"
                : $"{address}/128");
        }

        return excluded.Distinct().ToList();
    }
}
