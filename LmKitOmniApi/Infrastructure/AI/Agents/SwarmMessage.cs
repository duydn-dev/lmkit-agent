using MediatR;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

public class SwarmMessage : INotification
{
    public string TargetAgentId { get; set; } = string.Empty;
    public string SourceAgentId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
}
