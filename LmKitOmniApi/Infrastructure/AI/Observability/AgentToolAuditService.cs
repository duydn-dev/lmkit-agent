using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Infrastructure.AI.Observability;

public sealed class AgentToolAuditService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentToolAuditService> _logger;

    public AgentToolAuditService(IServiceScopeFactory scopeFactory, ILogger<AgentToolAuditService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(
        Guid tenantId,
        Guid? userId,
        Guid toolCallId,
        string toolName,
        string? parameters,
        string status,
        TimeSpan duration,
        Guid? approvalId = null,
        CancellationToken ct = default)
    {
        var argumentsHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(parameters ?? string.Empty)));

        var entry = new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            ActorType = "agent",
            Action = "AI.Tool.Invoke",
            EntityType = toolName.Length <= 100 ? toolName : toolName[..100],
            EntityId = approvalId,
            CorrelationId = toolCallId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                ArgumentsSha256 = argumentsHash,
                DurationMs = Math.Round(duration.TotalMilliseconds, 2),
                Status = status,
                ApprovalId = approvalId
            })
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            dbContext.AuditLogs.Add(entry);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist tool audit {ToolCallId} for {ToolName}.", toolCallId, toolName);
        }
    }
}
