using LmKitOmniApi.Application.Abstractions;
using MediatR;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Swarm Agent interface.
/// Enables Peer-to-Peer messaging between agents without a central orchestrator.
/// </summary>
public interface ISwarmAgent : ISpecializedAgent, INotificationHandler<SwarmMessage>
{
    string AgentId { get; }
    Task SendMessageAsync(string targetAgentId, string message, CancellationToken ct = default);
}

public abstract class BaseSwarmAgent : ISwarmAgent
{
    protected readonly IMediator _mediator;
    
    public abstract string AgentId { get; }
    public abstract string AgentName { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<string> SupportedCategories { get; }

    protected BaseSwarmAgent(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task SendMessageAsync(string targetAgentId, string message, CancellationToken ct = default)
    {
        await _mediator.Publish(new SwarmMessage
        {
            SourceAgentId = AgentId,
            TargetAgentId = targetAgentId,
            Content = message
        }, ct);
    }

    public async Task Handle(SwarmMessage notification, CancellationToken cancellationToken)
    {
        if (notification.TargetAgentId == AgentId)
        {
            await ReceiveMessageAsync(notification.SourceAgentId, notification.Content, cancellationToken);
        }
    }

    protected abstract Task ReceiveMessageAsync(string sourceAgentId, string message, CancellationToken ct);

    public abstract Task<double> EvaluateConfidenceAsync(string query, CancellationToken ct = default);
    
    public abstract Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? sessionId, string query, CancellationToken ct = default);
}
