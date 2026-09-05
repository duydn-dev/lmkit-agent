using DotNet.Testcontainers.Containers;
using LmKitOmniApi.Infrastructure.AI.Database;
using MySqlConnector;
using Testcontainers.MySql;

namespace LmKitOmniApi.IntegrationTests;

public sealed class MySqlFixture : DatabaseContainerFixture
{
    protected override IContainer Build() =>
        new MySqlBuilder().WithImage("mysql:8.0").Build();

    protected override async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE customers (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, city VARCHAR(100))";
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO customers (name, city) VALUES ('An','HN'), ('Binh','Hue'), ('Chi','DN'), ('Dung','HCM')";
        await insert.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Live MySQL proof (GAP 2), opt-in via Testcontainers. Skips when Docker is absent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlIntegrationTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    private static readonly IExternalDatabaseProvider Provider = new MySqlDatabaseProvider();

    public MySqlIntegrationTests(MySqlFixture fixture) => _fixture = fixture;

    // Returned name is a bare table in the current database.
    private static string BackupCountSql(string backup) => $"SELECT COUNT(*) FROM `{backup}`";

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
