using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pha 2: the agent's read-only database tool. Proves the core safety property
/// end-to-end in CI (SQLite target, no model): reads run and return rows, writes
/// are refused (never executed), DDL/unknown are refused, and the tool is
/// tenant-scoped. Schema retrieval is faked (no Qdrant/model).
/// </summary>
[Collection("DbSqlite")]
public sealed class DbQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _appConnection;
    private readonly HermesDbContext _db;
    private readonly string _targetDbPath;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DbConnectionSecretProtector _protector = new(new EphemeralDataProtectionProvider());

    public DbQueryServiceTests()
    {
        // In-memory app DB (holds the DatabaseConnection rows).
        _appConnection = new SqliteConnection("Data Source=:memory:");
        _appConnection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_appConnection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Test tenant" });
        _db.SaveChanges();

        // The external target DB the tool queries.
        _targetDbPath = Path.Combine(Path.GetTempPath(), $"lmkit-dbq-{Guid.NewGuid():N}.db");
        using var target = new SqliteConnection($"Data Source={_targetDbPath};Pooling=False");
        target.Open();
        using var cmd = target.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ('a'),('b');";
        cmd.ExecuteNonQuery();
    }

    private DbQueryService CreateService(bool enabled = true, string connectionName = "primary", bool allowWrites = false)
    {
        _db.DatabaseConnections.Add(new DatabaseConnection
        {
            TenantId = _tenantId,
            UserId = _userId,
            Name = connectionName,
            Provider = "Sqlite",
            ConnectionStringProtected = _protector.Protect($"Data Source={_targetDbPath};Pooling=False"),
            IsActive = true,
            IsIndexed = true,
            AllowWrites = allowWrites
        });
        _db.SaveChanges();

        var databases = new ExternalDatabaseService(
            new IExternalDatabaseProvider[] { new SqliteDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions { Enabled = enabled }));

        return new DbQueryService(
            _db,
            new FakeSchemaRetriever(),
            databases,
            _protector,
            Options.Create(new DatabaseAgentOptions { Enabled = enabled }),
            NullLogger<DbQueryService>.Instance);
    }

    [Fact]
    public async Task RunQuery_ReadOnly_ReturnsRows()
    {
        var result = await CreateService().RunQueryAsync(_tenantId, "SELECT id, name FROM t ORDER BY id", CancellationToken.None);

        Assert.Contains("id | name", result);
        Assert.Contains("1 | a", result);
        Assert.Contains("2 dòng", result);
    }

    [Theory]
    [InlineData("INSERT INTO t (name) VALUES ('z')")]
    [InlineData("UPDATE t SET name = 'z'")]
    [InlineData("DELETE FROM t")]
    public async Task RunQuery_Write_IsRefused_AndNotExecuted(string sql)
    {
        var service = CreateService();
        var result = await service.RunQueryAsync(_tenantId, sql, CancellationToken.None);

        Assert.Contains("cần người dùng phê duyệt", result);

        // Data untouched: still 2 rows.
        var check = await service.RunQueryAsync(_tenantId, "SELECT COUNT(*) FROM t", CancellationToken.None);
        Assert.Contains("2", check);
    }

    [Fact]
    public async Task RunQuery_Ddl_IsRefusedOutright()
    {
        var result = await CreateService().RunQueryAsync(_tenantId, "DROP TABLE t", CancellationToken.None);
        Assert.Contains("bị từ chối", result);
    }

    [Fact]
    public async Task RunQuery_ExecutionError_SchedulesReindex_OnAnIndexedConnection()
    {
        var service = CreateService(); // seeded with IsIndexed = true

        // A well-formed SELECT that passes the classifier but fails at execution because
        // the table does not exist — the signal that the indexed schema may be stale.
        var result = await service.RunQueryAsync(_tenantId, "SELECT * FROM nonexistent_table", CancellationToken.None);

        Assert.Contains("Truy vấn thất bại", result);
        Assert.Contains("lập chỉ mục lại", result);

        // The connection was flipped back to Pending so the worker re-introspects it.
        var row = _db.DatabaseConnections.AsNoTracking().Single(c => c.TenantId == _tenantId);
        Assert.False(row.IsIndexed);
        Assert.Equal("Pending", row.IndexStatus);
    }

    [Fact]
    public async Task GetSchema_ReturnsContext_AndReadOnlyGuidance()
    {
        var result = await CreateService().GetSchemaAsync(_tenantId, "khách hàng", CancellationToken.None);
        Assert.Contains("SCHEMA_CONTEXT_FOR", result);
        Assert.Contains("CHỈ-ĐỌC", result);
    }

    [Fact]
    public async Task RunWrite_NotAllowed_IsRefused_AndDataUntouched()
    {
        var service = CreateService(allowWrites: false);
        var result = await service.RunWriteAsync(_tenantId, "UPDATE t SET name = 'z' WHERE id = 1", CancellationToken.None);

        Assert.Contains("CHƯA bật ghi", result);
        var check = await service.RunQueryAsync(_tenantId, "SELECT name FROM t WHERE id = 1", CancellationToken.None);
        Assert.Contains("a", check); // still the original value
    }

    [Fact]
    public async Task RunWrite_Allowed_BacksUpTargetTable_ThenWrites()
    {
        var service = CreateService(allowWrites: true);
        var result = await service.RunWriteAsync(_tenantId, "UPDATE t SET name = 'z' WHERE id = 1", CancellationToken.None);

        Assert.Contains("Đã sao lưu", result);
        Assert.Contains("Số dòng ảnh hưởng: 1", result);

        // The write applied…
        var check = await service.RunQueryAsync(_tenantId, "SELECT name FROM t WHERE id = 1", CancellationToken.None);
        Assert.Contains("z", check);

        // …and a backup copy of the table now exists in the target DB.
        using var connection = new SqliteConnection($"Data Source={_targetDbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 't_backup_%'";
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 1, "A backup table must have been created before the write.");
    }

    [Fact]
    public async Task RunWrite_NonWriteStatement_IsRefused()
    {
        var result = await CreateService(allowWrites: true).RunWriteAsync(_tenantId, "SELECT * FROM t", CancellationToken.None);
        Assert.Contains("câu lệnh GHI", result);
    }

    [Fact]
    public async Task RunWrite_UndeterminableTarget_IsRefused_NoWrite()
    {
        // Classifies as a write (leading UPDATE) but the target can't be pinned → refuse before any backup/write.
        var result = await CreateService(allowWrites: true).RunWriteAsync(_tenantId, "UPDATE (SELECT 1) SET x = 1", CancellationToken.None);
        Assert.Contains("Không xác định được bảng", result);
    }

    [Fact]
    public async Task RunQuery_NoConnections_ReturnsFriendlyMessage()
    {
        // Service built with no connection row for a different tenant.
        var databases = new ExternalDatabaseService(
            new IExternalDatabaseProvider[] { new SqliteDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions { Enabled = true }));
        var service = new DbQueryService(_db, new FakeSchemaRetriever(), databases, _protector,
            Options.Create(new DatabaseAgentOptions { Enabled = true }), NullLogger<DbQueryService>.Instance);

        var result = await service.RunQueryAsync(Guid.NewGuid(), "SELECT 1", CancellationToken.None);
        Assert.Contains("Chưa có kết nối", result);
    }

    [Fact]
    public void IsEnabled_ReflectsOptions()
    {
        Assert.True(CreateService(enabled: true).IsEnabled);
        Assert.False(CreateService(enabled: false, connectionName: "secondary").IsEnabled);
    }

    public void Dispose()
    {
        _db.Dispose();
        _appConnection.Dispose();
        try { if (File.Exists(_targetDbPath)) File.Delete(_targetDbPath); } catch { /* best effort */ }
    }

    private sealed class FakeSchemaRetriever : ISchemaRetriever
    {
        public Task<string> RetrieveContextAsync(Guid tenantId, Guid connectionId, string nlQuery, int topK, CancellationToken ct) =>
            Task.FromResult($"SCHEMA_CONTEXT_FOR: {nlQuery}\nTable: t\n- id integer PRIMARY KEY\n- name text");
    }
}
