using DotNet.Testcontainers.Containers;
using LmKitOmniApi.Infrastructure.AI.Database;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace LmKitOmniApi.IntegrationTests;

public sealed class SqlServerFixture : DatabaseContainerFixture
{
    protected override IContainer Build() => new MsSqlBuilder().Build();

    protected override async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INT IDENTITY(1,1) PRIMARY KEY, name NVARCHAR(100) NOT NULL, city NVARCHAR(100));
            INSERT INTO customers (name, city) VALUES ('An','HN'), ('Binh','Hue'), ('Chi','DN'), ('Dung','HCM');
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Live SQL Server proof (GAP 2), opt-in via Testcontainers. Skips when Docker is absent.
/// SQL Server has no read-only transaction mode, so the read path always ROLLS BACK — the
/// server-level proof here is that a write on the read path leaves the row count unchanged.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerIntegrationTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;
    private static readonly IExternalDatabaseProvider Provider = new SqlServerDatabaseProvider();

    public SqlServerIntegrationTests(SqlServerFixture fixture) => _fixture = fixture;

    // Returned name is "lmkit_backup.<name>" → SELECT COUNT(*) FROM lmkit_backup.[<name>].
    private static string BackupCountSql(string backup)
    {
        var dot = backup.IndexOf('.');
        var name = dot < 0 ? backup : backup[(dot + 1)..];
        return $"SELECT COUNT(*) FROM lmkit_backup.[{name}]";
    }

    [SkippableFact]
    public async Task Write_OnReadPath_IsNotPersisted_ViaRollback()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        await RelationalIntegrationChecks.WriteOnReadPath_IsNotPersisted(Provider, _fixture.ConnectionString, expectServerRejection: false);
    }

    [SkippableFact]
    public async Task Backup_CopiesTargetTable()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        await RelationalIntegrationChecks.Backup_MakesRealCopyOfTargetTable(Provider, _fixture.ConnectionString, BackupCountSql);
    }

    [SkippableFact]
    public async Task Introspect_ReturnsSeededTables()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        await RelationalIntegrationChecks.Introspect_ReturnsSeededTable(Provider, _fixture.ConnectionString);
    }
}
