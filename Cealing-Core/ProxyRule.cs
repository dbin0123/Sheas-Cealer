namespace Cealing_Core;

public sealed class ProxyRule
{
    public string ServerName { get; set; } = string.Empty;
    public string Ip { get; set; } = "127.0.0.1";
    public string? Sni { get; set; }
    public bool SniEnabled { get; set; } = true;
    public int Port { get; set; } = 443;
}
