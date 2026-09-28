using Cealing_Core;
using Cealing_Core.Protocol;
using System.Collections.Generic;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class ProxyConfigSerializationTests
{
    [Fact]
    public void ProxyConfigRoundTrips()
    {
        ProxyConfig original = new()
        {
            Engine = ProxyEngineKind.External,
            Coproxy = true,
            WriteHosts = false,
            Flashing = true,
            HttpPort = 8080,
            HttpsPort = 8443,
            NginxBinaryPath = "/opt/app/Cealing-Nginx",
            NginxConfText = "http{}",
            CertSans = [new ProxyCertSan { Domain = "pixiv.net", Wildcard = true }],
            Rules = [new ProxyRule { ServerName = "^a\\.b$", Ip = "1.2.3.4", Sni = "cdn.example", SniEnabled = true, Port = 443 }]
        };

        ProxyConfig? restored = AgentJson.Deserialize<ProxyConfig>(AgentJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(ProxyEngineKind.External, restored.Engine);
        Assert.True(restored.Coproxy);
        Assert.True(restored.Flashing);
        Assert.Equal(8080, restored.HttpPort);
        Assert.Equal("/opt/app/Cealing-Nginx", restored.NginxBinaryPath);
        Assert.Equal("cdn.example", Assert.Single(restored.Rules).Sni);
        Assert.True(Assert.Single(restored.CertSans).Wildcard);
    }

    [Fact]
    public void NullFieldsAreOmitted()
    {
        string json = AgentJson.Serialize(new ProxyConfig());

        Assert.DoesNotContain("nginxBinaryPath", json);
        Assert.DoesNotContain("mihomoConfText", json);
    }

    [Fact]
    public void AgentRequestRoundTrips()
    {
        AgentResponse? response = AgentJson.Deserialize<AgentResponse>(
            AgentJson.Serialize(new AgentResponse
            {
                Ok = true,
                Status = new AgentStatus { Running = true, Engine = "builtin", HttpsPort = 443, RuleCount = 7 }
            }));

        Assert.NotNull(response);
        Assert.True(response.Ok);
        AgentStatus? status = response.Status;

        Assert.NotNull(status);
        Assert.Equal(7, status.RuleCount);
    }
}
