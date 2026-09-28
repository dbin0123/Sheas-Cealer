namespace Cealing_Core.Protocol;

public sealed class AgentResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AgentStatus? Status { get; set; }
}
