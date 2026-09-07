using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Interface for specialized agents in a multi-agent system.
/// Each agent has a specific expertise and can be delegated tasks.
/// Inspired by console_net/ai-agents/multi-agent-workflows + delegation.
///
/// <para>
/// ROUTING DOES NOT HAPPEN HERE. <c>MultiAgentOrchestrator</c> registers every
/// specialist as a worker of LM-Kit's <c>SupervisorOrchestrator</c> and the
/// supervisor model chooses, reading only <see cref="AgentName"/>,
/// <see cref="Description"/> and <see cref="SupportedCategories"/> — the worker
/// directory it is given. Those three members plus <see cref="ExecuteAsync"/>
/// are the entire contract; an agent-side scoring hook would never be consulted,
/// so none is declared.
/// </para>
/// </summary>
public interface ISpecializedAgent
{
    /// <summary>Unique name of this agent (e.g., "ResearchAgent", "VisionAgent").</summary>
    string AgentName { get; }

    /// <summary>Description of this agent's capabilities for routing decisions.</summary>
    string Description { get; }

    /// <summary>List of task categories this agent can handle.</summary>
    IReadOnlyList<string> SupportedCategories { get; }

    /// <summary>Execute the query and return a result context string.</summary>
    Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string userRole, string query, CancellationToken ct = default);
}

public class AgentExecutionResult
{
    public string AgentName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ResultContent { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<string> ToolsUsed { get; set; } = new();
    public TimeSpan Elapsed { get; set; }

    public static AgentExecutionResult Ok(string agentName, string content, List<string>? tools = null)
        => new() { AgentName = agentName, Success = true, ResultContent = content, ToolsUsed = tools ?? new() };

    public static AgentExecutionResult Fail(string agentName, string error)
        => new() { AgentName = agentName, Success = false, ErrorMessage = error };
}
