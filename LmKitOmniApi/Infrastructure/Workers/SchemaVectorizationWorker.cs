using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.Workers;

/// <summary>
/// Background indexer for external-database schemas. Mirrors
/// <see cref="DocumentVectorizationWorker"/>: polls connections needing a (re)index,
/// claims each with an atomic lease + attempt bump, introspects and indexes the
/// schema into its per-connection Qdrant collection, and records the outcome. A
/// connection is enqueued simply by setting IsIndexed=false (create replaces the
/// secret, or the explicit re-index endpoint).
/// </summary>
public sealed class SchemaVectorizationWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private const int MaximumAttempts = 3;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SchemaVectorizationWorker> _logger;

    public SchemaVectorizationWorker(IServiceProvider serviceProvider, ILogger<SchemaVectorizationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Job: Schema Vectorization Worker is starting.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

                var now = DateTime.UtcNow;
                var candidateIds = await dbContext.DatabaseConnections
                    .Where(c => !c.IsIndexed
                        && c.IsActive
                        && c.IndexAttempts < MaximumAttempts
                        && (c.IndexLeaseUntilUtc == null || c.IndexLeaseUntilUtc < now))
                    .OrderBy(c => c.CreatedAtUtc)
                    .Select(c => c.Id)
                    .Take(5)
                    .ToListAsync(stoppingToken);

                if (candidateIds.Count == 0) continue;

                var indexing = scope.ServiceProvider.GetRequiredService<SchemaIndexingService>();
                var protector = scope.ServiceProvider.GetRequiredService<DbConnectionSecretProtector>();
                var databases = scope.ServiceProvider.GetRequiredService<ExternalDatabaseService>();

                foreach (var connectionId in candidateIds)
                {
                    var leaseUntil = DateTime.UtcNow.Add(LeaseDuration);
                    var claimed = await dbContext.DatabaseConnections
                        .Where(c => c.Id == connectionId
                            && !c.IsIndexed
                            && c.IndexAttempts < MaximumAttempts
                            && (c.IndexLeaseUntilUtc == null || c.IndexLeaseUntilUtc < DateTime.UtcNow))
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(c => c.IndexStatus, "Processing")
                            .SetProperty(c => c.IndexLeaseUntilUtc, leaseUntil)
                            .SetProperty(c => c.IndexAttempts, c => c.IndexAttempts + 1)
                            .SetProperty(c => c.LastIndexError, (string?)null), stoppingToken);
                    if (claimed != 1) continue;

                    var connection = await dbContext.DatabaseConnections.SingleAsync(c => c.Id == connectionId, stoppingToken);

                    // MongoDB is schemaless — never SQL-introspected. Should never reach the
                    // worker (created IsIndexed=true), but if one does, complete it in place
                    // rather than failing on an unsupported-provider introspection.
                    if (MongoDatabaseService.Handles(connection.Provider))
                    {
                        connection.IsIndexed = true;
                        connection.IndexStatus = "Completed";
                        connection.IndexLeaseUntilUtc = null;
                        connection.LastIndexError = null;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    try
                    {
                        if (!databases.TryParseProvider(connection.Provider, out var provider))
                            throw new InvalidOperationException($"Loại cơ sở dữ liệu không được hỗ trợ: {connection.Provider}.");

                        var connectionString = protector.Unprotect(connection.ConnectionStringProtected);
                        var tableCount = await indexing.IndexAsync(provider, connectionString, connection.TenantId, connection.Id, stoppingToken);

                        connection.IsIndexed = true;
                        connection.IndexStatus = "Completed";
                        connection.LastIndexedAtUtc = DateTime.UtcNow;
                        connection.IndexLeaseUntilUtc = null;
                        connection.LastIndexError = null;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation("Indexed schema for connection {ConnectionId} ({Tables} tables).", connection.Id, tableCount);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        connection.IndexStatus = "Failed";
                        connection.IndexLeaseUntilUtc = null;
                        var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                        connection.LastIndexError = message;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogError(ex, "Schema indexing failed for connection {ConnectionId}.", connection.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in schema vectorization worker.");
            }
        }
    }
}
