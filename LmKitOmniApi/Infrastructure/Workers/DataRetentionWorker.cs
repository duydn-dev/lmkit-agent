using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Infrastructure.Workers;

/// <summary>Runs bounded retention jobs without relying on an external scheduler.</summary>
public sealed class DataRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionWorker> _logger;

    public DataRetentionWorker(IServiceScopeFactory scopeFactory, ILogger<DataRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunCleanupAsync(stoppingToken);
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var memoryService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();
            await memoryService.CleanupExpiredMemoriesAsync(ct);

            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            var mcpHeaderProtector = scope.ServiceProvider.GetRequiredService<McpHeaderProtector>();
            var legacyMcpServers = await db.ExternalMcpServers
                .Where(server => server.HeadersJson != null && !server.HeadersJson.StartsWith("dp:v1:"))
                .ToListAsync(ct);
            foreach (var server in legacyMcpServers)
                server.HeadersJson = mcpHeaderProtector.Protect(server.HeadersJson!);

            var sessionCutoff = DateTime.UtcNow.AddDays(-30);
            var deletedSessions = await db.UserSessions
                .Where(session => session.ExpiresAtUtc < DateTime.UtcNow
                    || (session.RevokedAtUtc != null && session.RevokedAtUtc < sessionCutoff))
                .ExecuteDeleteAsync(ct);
            if (legacyMcpServers.Count > 0) await db.SaveChangesAsync(ct);
            _logger.LogInformation("Retention cleanup completed; removed {SessionCount} stale sessions", deletedSessions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention cleanup failed");
        }
    }
}
