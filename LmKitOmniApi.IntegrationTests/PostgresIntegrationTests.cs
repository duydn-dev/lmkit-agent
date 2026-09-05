using DotNet.Testcontainers.Containers;
using LmKitOmniApi.Infrastructure.AI.Database;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LmKitOmniApi.IntegrationTests;

public sealed class PostgresFixture : DatabaseContainerFixture
{
    protected override IContainer Build() =>
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    protected override async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, city text);
            INSERT INTO customers (name, city) VALUES ('An','HN'), ('Binh','Hue'), ('Chi','DN'), ('Dung','HCM');
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Live PostgreSQL proof (GAP 2), opt-in via Testcontainers. Skips when Docker is absent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly IExternalDatabaseProvider Provider = new PostgresDatabaseProvider();

    public PostgresIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    // Returned name is "lmkit_backup.<table>" → SELECT COUNT(*) FROM lmkit_backup."<table>".
    private static string BackupCountSql(string backup)
    {
        var dot = backup.IndexOf('.');
        var name = dot < 0 ? backup : backup[(dot + 1)..];
        return $"SELECT COUNT(*) FROM lmkit_backup.\"{name}\"";
    }

    [SkippableFact]
    public async Task Write_OnReadPath_IsRejectedAtServer()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        await RelationalIntegrationChecks.WriteOnReadPath_IsNotPersisted(Provider, _fixture.ConnectionString, expectServerRejection: true);
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
