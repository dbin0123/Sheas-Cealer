using System.Collections.Generic;

namespace Cealing_Core;

public sealed class ProxyConfig
{
    public ProxyEngineKind Engine { get; set; } = ProxyEngineKind.Builtin;
    public bool Coproxy { get; set; }
    public bool Flashing { get; set; }
    public bool WriteHosts { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public int MixedPort { get; set; } = 7880;
    public string? NginxBinaryPath { get; set; }
    public string? NginxConfText { get; set; }
    public string? MihomoBinaryPath { get; set; }
    public string? MihomoConfText { get; set; }
    public List<ProxyCertSan> CertSans { get; set; } = [];
    public List<ProxyRule> Rules { get; set; } = [];
}
