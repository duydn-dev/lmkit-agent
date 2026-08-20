using System.Net.Sockets;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

public sealed class QdrantHealthCheck : IHealthCheck
{
    private readonly Uri _endpoint;
    public QdrantHealthCheck(IConfiguration configuration)
    {
        _endpoint = new Uri(configuration["VectorStore:BaseUrl"] ?? "http://localhost:6334");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_endpoint.Host, _endpoint.Port, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Qdrant is unreachable.", ex);
        }
    }
}
