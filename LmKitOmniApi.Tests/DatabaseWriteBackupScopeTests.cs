using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// GAP 3 (backup-scope honesty): the approved-write path backs up ONLY the directly
/// targeted table, so a write that can reach OTHER tables via ON DELETE/UPDATE CASCADE
/// foreign keys or triggers is NOT fully recoverable from that backup. Rather than
/// promise false recoverability, the write is REFUSED when such side effects exist.
/// Proven live against a real temp SQLite file (no Docker): a triggered table and a
/// cascade-parent table are both refused, while a plain table still backs up and writes.
/// </summary>
[Collection("DbSqlite")]
public sealed class DatabaseWriteBackupScopeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public DatabaseWriteBackupScopeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lmkit-dbscope-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            -- plain, self-contained target: safe to back up as a single table.
            CREATE TABLE plain_items (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO plain_items (name) VALUES ('a'), ('b'), ('c');

            -- a trigger on 'audited' writes ANOTHER table on update → single-table backup is incomplete.
            CREATE TABLE audit_log (id INTEGER PRIMARY KEY, msg TEXT);
            CREATE TABLE audited (id INTEGER PRIMARY KEY, val TEXT);
            INSERT INTO audited (val) VALUES ('x'), ('y');
            CREATE TRIGGER audited_after_update AFTER UPDATE ON audited
                BEGIN INSERT INTO audit_log (msg) VALUES ('audited changed'); END;

            -- 'child' references 'parent' ON DELETE CASCADE → deleting a parent row modifies 'child'.
            CREATE TABLE parent (id INTEGER PRIMARY KEY, label TEXT);
            CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE);
            INSERT INTO parent (label) VALUES ('p1'), ('p2');
            INSERT INTO child (parent_id) VALUES (1), (2);
            """;
        command.ExecuteNonQuery();
    }

    private static ExternalDatabaseService CreateService() =>
        new(
            new IExternalDatabaseProvider[] { new SqliteDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions()));

    [Fact]
    public async Task ApprovedWrite_OnTriggeredTable_IsRefused_AndNothingChanges()
    {
        var ex = await Assert.ThrowsAsync<DatabaseOperationRefusedException>(() =>
            CreateService().ExecuteApprovedWriteAsync(
                DbProvider.Sqlite, _connectionString, "UPDATE audited SET val = 'z' WHERE id = 1", CancellationToken.None));

        Assert.Contains("trigger", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The write never ran and no backup table was created.
        Assert.Equal("x", ScalarString("SELECT val FROM audited WHERE id = 1"));
        Assert.Equal(0, BackupTableCount());
    }

    [Fact]
    public async Task ApprovedWrite_OnCascadeParent_IsRefused_AndNothingChanges()
    {
        var ex = await Assert.ThrowsAsync<DatabaseOperationRefusedException>(() =>
            CreateService().ExecuteApprovedWriteAsync(
                DbProvider.Sqlite, _connectionString, "DELETE FROM parent WHERE id = 1", CancellationToken.None));

        Assert.Contains("khóa ngoại", ex.Message); // FK cascade flagged
        Assert.Equal("2", ScalarString("SELECT COUNT(*) FROM parent"));
        Assert.Equal(0, BackupTableCount());
    }

    [Fact]
    public async Task ApprovedWrite_OnPlainTable_BacksUp_ThenWrites()
    {
        var summary = await CreateService().ExecuteApprovedWriteAsync(
            DbProvider.Sqlite, _connectionString, "UPDATE plain_items SET name = 'z' WHERE id = 1", CancellationToken.None);

        Assert.Contains("Đã sao lưu", summary);
        Assert.Contains("Số dòng ảnh hưởng: 1", summary);
        Assert.Equal("z", ScalarString("SELECT name FROM plain_items WHERE id = 1"));
        Assert.True(BackupTableCount() >= 1, "A single-table backup of plain_items must exist.");
    }

    [Fact]
    public async Task DetectWriteSideEffects_DistinguishesSafeFromRisky()
    {
        var provider = new SqliteDatabaseProvider();

        Assert.Empty(await provider.DetectWriteSideEffectsAsync(_connectionString, "plain_items", 10, CancellationToken.None));
        Assert.NotEmpty(await provider.DetectWriteSideEffectsAsync(_connectionString, "audited", 10, CancellationToken.None));
        Assert.NotEmpty(await provider.DetectWriteSideEffectsAsync(_connectionString, "parent", 10, CancellationToken.None));
    }

    private string? ScalarString(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private int BackupTableCount()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE '%backup%'";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
