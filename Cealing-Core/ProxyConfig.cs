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

    // 自动发现扫不到的 Java 信任库，由用户在设置页补充（cacerts 文件或 JDK/JBR 根目录）。
    // 放在这里而不是让 agent 自己去读用户配置：agent 以 root 运行，
    // 用户配置文件的位置和内容对它来说是 GUI 的实现细节。
    public List<string> JavaTrustPaths { get; set; } = [];
}
