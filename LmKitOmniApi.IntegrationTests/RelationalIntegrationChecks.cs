using LmKitOmniApi.Infrastructure.AI.Database;

namespace LmKitOmniApi.IntegrationTests;

/// <summary>
/// The three GAP-2 proofs, shared by every relational engine and run against a REAL
/// container. Providers are exercised DIRECTLY (not via <see cref="ExternalDatabaseService"/>)
/// because that service's SSRF egress guard blocks the loopback/private address a local
/// container is reachable on — so calling the provider is how we reach a live local engine.
/// </summary>
internal static class RelationalIntegrationChecks
{
    /// <summary>
    /// (a) A write on the READ path does not persist — proven at the SERVER level, not by
    /// the classifier (which we bypass here). Postgres/MySQL/Oracle reject it inside a
    /// read-only transaction (<paramref name="expectServerRejection"/> = true); SQL Server
    /// has no read-only mode and instead always rolls the transaction back, so there the
    /// proof is that the row count is unchanged afterwards.
    /// </summary>
    public static async Task WriteOnReadPath_IsNotPersisted(
        IExternalDatabaseProvider provider, string connectionString, bool expectServerRejection)
    {
        var before = await CountAsync(provider, connectionString, "SELECT COUNT(*) FROM customers");

        var rejected = false;
        try
        {
            await provider.ExecuteReadOnlyAsync(connectionString, "DELETE FROM customers", 1000, 30, CancellationToken.None);
        }
        catch
        {
            rejected = true; // server refused the write on the read-only path
        }

        if (expectServerRejection)
            Assert.True(rejected, "A write on the read path must be rejected by the server (read-only transaction).");

        var after = await CountAsync(provider, connectionString, "SELECT COUNT(*) FROM customers");
        Assert.Equal(before, after); // never persisted, whether rejected outright or rolled back
    }

    /// <summary>(b) Backing up the target table produces a real, independent copy of its rows.</summary>
    public static async Task Backup_MakesRealCopyOfTargetTable(
        IExternalDatabaseProvider provider, string connectionString, Func<string, string> backupCountSql)
    {
        var original = await CountAsync(provider, connectionString, "SELECT COUNT(*) FROM customers");
        Assert.True(original > 0, "seed must have inserted rows");

        var backup = await provider.BackupTableAsync(connectionString, "customers", 30, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(backup));

        var copied = await CountAsync(provider, connectionString, backupCountSql(backup));
        Assert.Equal(original, copied);
    }

    /// <summary>(c) Schema introspection returns the seeded table.</summary>
    public static async Task Introspect_ReturnsSeededTable(
        IExternalDatabaseProvider provider, string connectionString)
    {
        var tables = await provider.IntrospectAsync(connectionString, 30, CancellationToken.None);
        Assert.Contains(tables, t => t.Name.Equals("customers", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<long> CountAsync(IExternalDatabaseProvider provider, string connectionString, string sql)
    {
        var result = await provider.ExecuteReadOnlyAsync(connectionString, sql, 10, 30, CancellationToken.None);
        return long.Parse(result.Rows[0][0]!);
    }
}
