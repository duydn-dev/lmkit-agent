namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Filter pipeline interface for AI agent input/output processing.
/// Inspired by console_net/ai-agents/filter-pipeline pattern.
/// </summary>
public interface IAgentFilter
{
    /// <summary>Order of execution. Lower = earlier.</summary>
    int Order { get; }
    
    /// <summary>Pre-process the user input before it reaches the AI model.</summary>
    Task<AgentFilterResult> OnInputAsync(AgentFilterContext context, CancellationToken ct = default);
    
    /// <summary>Post-process the AI output before it reaches the user.</summary>
    Task<AgentFilterResult> OnOutputAsync(AgentFilterContext context, CancellationToken ct = default);
}

public class AgentFilterContext
{
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string UserRole { get; set; } = "User";
    public string OriginalInput { get; set; } = string.Empty;
    public string ProcessedInput { get; set; } = string.Empty;
    public string? Output { get; set; }
    public string? ToolName { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class AgentFilterResult
{
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public string ProcessedContent { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    
    public static AgentFilterResult Pass(string content) => new() { ProcessedContent = content };
    public static AgentFilterResult Block(string reason) => new() { IsBlocked = true, BlockReason = reason };
}
