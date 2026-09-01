using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// End-to-end read-only pipeline against a real temp SQLite file (no Docker): the
/// classifier refuses writes, the provider enforces read-only at the connection
/// level, and row/output caps apply. Proves the DB agent's read path is safe in CI.
/// </summary>
public sealed class ExternalDatabaseServiceSqliteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public ExternalDatabaseServiceSqliteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lmkit-dbtest-{Guid.NewGuid():N}.db");
        // Pooling=False so no connection lingers in the shared pool holding the file
        // handle open — the temp file stays deletable and no global pool reset is needed.
        _connectionString = $"Data Source={_dbPath};Pooling=False";

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            INSERT INTO customers (name, city) VALUES ('An','Hà Nội'),('Bình','Huế'),('Chi','Đà Nẵng'),('Dũng','TP.HCM'),('Em','Cần Thơ');
            """;
        command.ExecuteNonQuery();
    }

    private static ExternalDatabaseService CreateService(int maxRows = 500) =>
        new(
            new IExternalDatabaseProvider[] { new SqliteDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions { MaxRows = maxRows }));

    [Fact]
    public async Task QueryReadOnly_ReturnsRows_ForASelect()
    {
        var result = await CreateService().QueryReadOnlyAsync(
            DbProvider.Sqlite, _connectionString, "SELECT id, name, city FROM customers ORDER BY id", CancellationToken.None);

        Assert.Equal(new[] { "id", "name", "city" }, result.Columns);
        Assert.Equal(5, result.Rows.Count);
        Assert.False(result.Truncated);
        Assert.Equal("An", result.Rows[0][1]);
    }

    [Fact]
    public async Task QueryReadOnly_CapsRows_AndFlagsTruncation()
    {
        var result = await CreateService(maxRows: 2).QueryReadOnlyAsync(
            DbProvider.Sqlite, _connectionString, "SELECT * FROM customers", CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("INSERT INTO customers (name) VALUES ('Zzz')")]
    [InlineData("UPDATE customers SET city = 'X'")]
    [InlineData("DELETE FROM customers")]
    [InlineData("DROP TABLE customers")]
    public async Task QueryReadOnly_RefusesNonReadStatements(string sql)
    {
        await Assert.ThrowsAsync<DatabaseOperationRefusedException>(() =>
            CreateService().QueryReadOnlyAsync(DbProvider.Sqlite, _connectionString, sql, CancellationToken.None));

        // And the data is untouched.
        var check = await CreateService().QueryReadOnlyAsync(
            DbProvider.Sqlite, _connectionString, "SELECT COUNT(*) FROM customers", CancellationToken.None);
        Assert.Equal("5", check.Rows[0][0]);
    }

    [Fact]
    public async Task Provider_ReadOnlyConnection_BlocksAWrite_EvenIfClassifierWereBypassed()
    {
        // Call the provider directly (skipping the service's classifier) to prove the
        // connection-level read-only mode is an independent backstop.
        var provider = new SqliteDatabaseProvider();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.ExecuteReadOnlyAsync(_connectionString, "INSERT INTO customers (name) VALUES ('X')", 100, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Introspect_ReturnsTablesColumnsAndKeys()
    {
        var provider = new SqliteDatabaseProvider();
        var tables = await provider.IntrospectAsync(_connectionString, 10, CancellationToken.None);

        var customers = Assert.Single(tables, t => t.Name == "customers");
        Assert.Contains(customers.Columns, c => c.Name == "id" && c.IsPrimaryKey);
        Assert.Contains(customers.Columns, c => c.Name == "name" && !c.IsNullable);
    }

    [Fact]
    public async Task TestConnection_Succeeds_ForAValidSqliteFile()
    {
        var denial = await CreateService().TestConnectionAsync(DbProvider.Sqlite, _connectionString, CancellationToken.None);
        Assert.Null(denial); // no egress denial, no throw
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
