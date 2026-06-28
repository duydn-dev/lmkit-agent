using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Interface for specialized agents in a multi-agent system.
/// Each agent has a specific expertise and can be delegated tasks.
/// Inspired by console_net/ai-agents/multi-agent-workflows + delegation.
/// </summary>
public interface ISpecializedAgent
{
    /// <summary>Unique name of this agent (e.g., "ResearchAgent", "VisionAgent").</summary>
    string AgentName { get; }

    /// <summary>Description of this agent's capabilities for routing decisions.</summary>
    string Description { get; }

    /// <summary>List of task categories this agent can handle.</summary>
    IReadOnlyList<string> SupportedCategories { get; }

    /// <summary>Evaluate how confident this agent is for handling the given query (0.0–1.0).</summary>
    Task<double> EvaluateConfidenceAsync(string query, CancellationToken ct = default);

    /// <summary>Execute the query and return a result context string.</summary>
    Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct = default);
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
