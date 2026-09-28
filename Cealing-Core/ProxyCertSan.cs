namespace Cealing_Core;

public sealed class ProxyCertSan
{
    public string Domain { get; set; } = string.Empty;
    public bool Wildcard { get; set; }
}
