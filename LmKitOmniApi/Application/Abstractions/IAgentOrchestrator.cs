namespace LmKitOmniApi.Application.Abstractions;

public interface IAgentOrchestrator
{
    Task<string> ProcessQueryAsync(Guid tenantId, string query);
    IAsyncEnumerable<string> StreamProcessQueryAsync(Guid tenantId, Guid sessionId, Guid userId, string query, LMKit.TextGeneration.Chat.ChatHistory history, CancellationToken cancellationToken);
    Task<List<string>> DecomposeTaskAsync(string query);
    Task<string> RouteQueryAsync(string query);
    Task<string> ExecuteDirectActionAsync(Guid tenantId, Guid userId, string action, string query, CancellationToken ct = default);
}
