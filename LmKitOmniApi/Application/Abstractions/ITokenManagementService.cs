namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Service for managing token budgets and conversation context windows.
/// Prevents context overflow by implementing sliding window with summary.
/// </summary>
public interface ITokenManagementService
{
    /// <summary>Estimate token count for a string.</summary>
    int EstimateTokenCount(string text);

    /// <summary>
    /// Trim chat history to fit within token budget using sliding window strategy.
    /// Returns the trimmed messages and an optional summary of removed messages.
    /// </summary>
    Task<TrimmedHistoryResult> TrimHistoryAsync(List<HistoryMessage> messages, int maxTokenBudget, CancellationToken ct = default);
}

public class HistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class TrimmedHistoryResult
{
    /// <summary>Messages that fit within the token budget.</summary>
    public List<HistoryMessage> Messages { get; set; } = new();
    
    /// <summary>Summary of messages that were removed to save tokens.</summary>
    public string? ConversationSummary { get; set; }
    
    /// <summary>Number of messages that were removed.</summary>
    public int RemovedMessageCount { get; set; }
    
    /// <summary>Estimated total tokens of the trimmed history.</summary>
    public int EstimatedTokenCount { get; set; }
}
