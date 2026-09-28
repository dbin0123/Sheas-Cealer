namespace Cealing_Core.Protocol;

public sealed class AgentRequest
{
    public string Command { get; set; } = string.Empty;
    public string? Argument { get; set; }
}
