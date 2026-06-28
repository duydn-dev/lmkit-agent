namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Service for managing persistent agent memory.
/// Enables the agent to remember facts, preferences, and context across sessions.
/// Inspired by console_net/ai-agents/agent-memory.
/// </summary>
public interface IAgentMemoryService
{
    /// <summary>Store a new memory or update an existing one.</summary>
    Task<Guid> StoreMemoryAsync(Guid tenantId, Guid? userId, string memoryType, string key, string value, 
        string? sourceContext = null, float confidence = 0.5f, DateTime? expiresAt = null, CancellationToken ct = default);

    /// <summary>Recall relevant memories for a given query.</summary>
    Task<List<MemoryRecallResult>> RecallMemoriesAsync(Guid tenantId, Guid? userId, string query, int maxResults = 5, CancellationToken ct = default);

    /// <summary>Extract and store facts from a conversation turn.</summary>
    Task ExtractAndStoreFactsAsync(Guid tenantId, Guid? userId, string userMessage, string assistantResponse, CancellationToken ct = default);
    
    /// <summary>Get a formatted memory context string to inject into system prompt.</summary>
    Task<string> GetMemoryContextAsync(Guid tenantId, Guid? userId, string currentQuery, CancellationToken ct = default);
    
    /// <summary>Clean up expired memories.</summary>
    Task CleanupExpiredMemoriesAsync(CancellationToken ct = default);
}

public class MemoryRecallResult
{
    public Guid Id { get; set; }
    public string MemoryType { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public double RelevanceScore { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
