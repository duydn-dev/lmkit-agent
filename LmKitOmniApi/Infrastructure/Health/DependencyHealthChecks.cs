using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.VectorDb;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;

namespace LmKitOmniApi.Infrastructure.Health;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    public PostgresHealthCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unreachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL health check failed.", ex);
        }
    }
}

/// <summary>
/// Liveness probe for Qdrant.
/// <para>
/// The type is NOT DI-registered, so <c>ActivatorUtilities</c> re-creates it on
/// every health poll. It therefore must not own a client: constructing a
/// <see cref="QdrantClient"/> per instance leaked one gRPC channel (and its
/// connection/keepalive machinery) per poll, forever. It now borrows the
/// process-lifetime shared client instead — one channel for the whole process,
/// and it deliberately does not dispose it.
/// </para>
/// </summary>
public sealed class QdrantHealthCheck : IHealthCheck
{
    private readonly QdrantClient _client;
    public QdrantHealthCheck(IConfiguration configuration) =>
        _client = QdrantClientFactory.Shared(configuration);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListCollectionsAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Qdrant is unreachable.", ex);
        }
    }
}
