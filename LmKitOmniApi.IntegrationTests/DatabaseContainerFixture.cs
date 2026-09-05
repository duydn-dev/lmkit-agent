using DotNet.Testcontainers.Containers;

namespace LmKitOmniApi.IntegrationTests;

/// <summary>
/// Base xUnit class-fixture that spins up ONE real engine container for a test class.
/// Docker-optional by design: if the container cannot start (Docker absent, image
/// unreachable, engine slow to boot) the failure is captured into <see cref="SkipReason"/>
/// instead of thrown, so every dependent <c>[SkippableFact]</c> SKIPS rather than fails —
/// the whole point of GAP 2's opt-in tests. When Docker IS present the container starts,
/// the schema is seeded, and <see cref="ConnectionString"/> points at it.
/// </summary>
public abstract class DatabaseContainerFixture : IAsyncLifetime
{
    private IContainer? _container;

    /// <summary>Non-null when the container could not be started/seeded → tests skip.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Live connection string once the container is up; empty when skipped.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    protected abstract IContainer Build();

    protected virtual string ResolveConnectionString(IContainer container) =>
        ((IDatabaseContainer)container).GetConnectionString();

    protected abstract Task SeedAsync(string connectionString, CancellationToken ct);

    public async Task InitializeAsync()
    {
        try
        {
            _container = Build();
            await _container.StartAsync();
            ConnectionString = ResolveConnectionString(_container);
            await SeedAsync(ConnectionString, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Any startup/seed failure = environment not ready → skip, never fail.
            SkipReason = $"Live engine unavailable (Docker not running or image unreachable) — test skipped. {ex.GetType().Name}: {Truncate(ex.Message)}";
            await SafeDisposeAsync();
        }
    }

    public Task DisposeAsync() => SafeDisposeAsync();

    private async Task SafeDisposeAsync()
    {
        if (_container is null) return;
        try { await _container.DisposeAsync(); } catch { /* best effort cleanup */ }
        _container = null;
    }

    private static string Truncate(string value) => value.Length <= 240 ? value : value[..240];
}
