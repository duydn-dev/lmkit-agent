namespace LmKitOmniApi.Application.Abstractions;

public interface IAgentOrchestrator
{
    Task<string> ProcessQueryAsync(Guid tenantId, string query);
    IAsyncEnumerable<string> StreamProcessQueryAsync(Guid tenantId, string query, LMKit.TextGeneration.Chat.ChatHistory history, CancellationToken cancellationToken);
    Task<List<string>> DecomposeTaskAsync(string query);
    Task<string> RouteQueryAsync(string query);
}
