namespace LmKitOmniApi.Application.Abstractions;

public interface IAgentOrchestrator
{
    IAsyncEnumerable<string> StreamProcessQueryAsync(Guid tenantId, Guid sessionId, Guid userId, string userRole, string query, LMKit.TextGeneration.Chat.ChatHistory history, CancellationToken cancellationToken);
    Task<string> ExecuteDirectActionAsync(Guid tenantId, Guid userId, string action, string query, Guid? approvalId = null, CancellationToken ct = default);
}
