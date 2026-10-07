using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Sheas_Cealer_Nix.Utils;

// mihomo 的 auto-route 在 Linux 下会下发 `from 0.0.0.0 iif lo lookup <tun 表>`，
// 把本机**所有进程**发起的出站都交给 TUN。引擎的回源是 agent 进程直连规则里的 IP，
// 一旦被吸进 TUN 就成了回环：mihomo 判它 DIRECT，再发出的同一条连接又命中同一条规则，
// 永远上不了物理网卡。表现为 BuiltinEngineRule 的 ConnectTimeout 到点
// （浏览器侧 HTTP 502），全局模式下 447 个域名全部回源失败 = 整机断网。
//
// 规则里的 IP 是引擎自己的上游，本来就该绕过 TUN，所以逐个写进 route-exclude-address。
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
