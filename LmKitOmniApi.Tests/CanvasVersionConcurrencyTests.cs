using LmKitOmniApi.Application.Canvas.Commands;
using LmKitOmniApi.Application.Canvas.Handlers;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Version-race guarantees for the canvas version chain. These run against a real
/// SQLite <see cref="HermesDbContext"/> built with <c>EnsureCreated</c>, so the UNIQUE
/// index on (TenantId, RootId, Version) is actually enforced — the same guard that
/// backs <see cref="UpdateCanvasArtifactCommandHandler"/>'s retry. Deterministic and
/// model-free: no native engine, no network.
/// </summary>
[Collection("DbSqlite")]
public sealed class CanvasVersionConcurrencyTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SqliteConnection OpenConnection()
    {
        // A single kept-open in-memory connection IS the database for the test; every
        // context created on it sees the same rows (the documented EF Core SQLite
        // in-memory testing pattern).
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static HermesDbContext NewContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(connection).Options);

    /// <summary>Creates the schema and the tenant + user the canvas rows' FKs require.</summary>
    private static void SeedSchemaAndOwner(HermesDbContext db)
    {
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Test tenant" });
        db.Users.Add(new User
        {
            Id = UserId,
            TenantId = TenantId,
            Username = "canvas-user",
            Email = "canvas-owner@example.test",
            PasswordHash = "x",
            FullName = "Canvas User",
        });
        db.SaveChanges();
    }

    private static CanvasArtifact Row(Guid rootId, int version, string content, string title = "Doc") => new()
    {
        Id = version == 1 ? rootId : Guid.NewGuid(), // v1: the root IS the row (mirrors the create handler)
        RootId = rootId,
        TenantId = TenantId,
        UserId = UserId,
        Title = title,
        Kind = "markdown",
        Language = null,
        Content = content,
        Version = version,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static UpdateCanvasArtifactCommand Update(Guid rootId, string content, string? title = null) => new()
    {
        TenantId = TenantId,
        UserId = UserId,
        RootId = rootId,
        Title = title,
        Content = content,
    };

    // ── 1. The DB-level guard actually exists (proves the unique index is created) ──

    [Fact]
    public void UniqueIndex_RejectsDuplicateRootVersion()
    {
        using var connection = OpenConnection();
        using var db = NewContext(connection);
        SeedSchemaAndOwner(db);

        var rootId = Guid.NewGuid();
        db.CanvasArtifacts.Add(Row(rootId, version: 1, content: "v1"));
        db.SaveChanges();

        // A second row with a DISTINCT PK but the SAME (TenantId, RootId, Version) — so
        // ONLY the unique index can reject it (not the primary key) — must fail at the DB.
        db.CanvasArtifacts.Add(new CanvasArtifact
        {
            Id = Guid.NewGuid(),
            RootId = rootId,
            TenantId = TenantId,
            UserId = UserId,
            Title = "duplicate",
            Kind = "markdown",
            Content = "duplicate",
            Version = 1,
            CreatedAtUtc = DateTime.UtcNow,
        });

        var ex = Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        Assert.NotNull(ex.InnerException); // provider-level UNIQUE-constraint violation
    }

    // ── 2. The race: a concurrent save takes version N+1, the handler retries to N+2 ──

    /// <summary>
    /// A <see cref="HermesDbContext"/> that, on its FIRST save only, lets a supplied
    /// "concurrent" writer commit first — reproducing a racing save that lands between
    /// this handler's read and its write. Deterministic stand-in for two threads.
    /// </summary>
    private sealed class RacingSaveDbContext : HermesDbContext
    {
        private readonly Action _commitRacingSaveOnce;
        private bool _raced;

        public RacingSaveDbContext(DbContextOptions<HermesDbContext> options, Action commitRacingSaveOnce)
            : base(options) => _commitRacingSaveOnce = commitRacingSaveOnce;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced)
            {
                _raced = true;
                _commitRacingSaveOnce(); // the other writer commits Version+1 first
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ConcurrentUpdate_LosingWriter_RetriesToNextSequentialVersion()
    {
        using var connection = OpenConnection();
        var rootId = Guid.NewGuid();
        using (var seed = NewContext(connection))
        {
            SeedSchemaAndOwner(seed);
            seed.CanvasArtifacts.Add(Row(rootId, version: 1, content: "v1"));
            seed.SaveChanges();
        }

        // The racing writer that grabs version 2 in the window between the handler's
        // read (sees v1) and the handler's write (tries v2 → loses on the unique index).
        void CommitRacingVersion2()
        {
            using var other = NewContext(connection);
            other.CanvasArtifacts.Add(Row(rootId, version: 2, content: "from-racing-writer"));
            other.SaveChanges();
        }

        using var handlerContext = new RacingSaveDbContext(
            new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(connection).Options,
            CommitRacingVersion2);
        var handler = new UpdateCanvasArtifactCommandHandler(handlerContext);

        var result = await handler.Handle(Update(rootId, content: "from-handler"), CancellationToken.None);

        // The handler read v1, lost the v2 slot to the racing writer, caught the
        // unique-constraint violation, re-read (now v2) and retried to v3.
        Assert.NotNull(result);
        Assert.Equal(3, result!.Version);

        // Both saves survived with DISTINCT SEQUENTIAL versions and no (RootId, Version)
        // duplicate — the whole point of the fix.
        using var verify = NewContext(connection);
        var rows = verify.CanvasArtifacts.AsNoTracking()
            .Where(c => c.RootId == rootId)
            .OrderBy(c => c.Version)
            .ToList();

        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Version).ToArray());
        Assert.Equal(rows.Count, rows.Select(r => r.Version).Distinct().Count()); // no dup
        Assert.Equal("from-racing-writer", rows.Single(r => r.Version == 2).Content);
        Assert.Equal("from-handler", rows.Single(r => r.Version == 3).Content);
    }

    // ── 3. The ordinary path is untouched: sequential edits keep incrementing and
    //       every old version stays readable through the real read handler ──

    [Fact]
    public async Task SequentialEdits_IncrementVersions_AndOldVersionsRemainReadable()
    {
        using var connection = OpenConnection();
        using var db = NewContext(connection);
        SeedSchemaAndOwner(db);

        var rootId = Guid.NewGuid();
        db.CanvasArtifacts.Add(Row(rootId, version: 1, content: "v1", title: "Original"));
        db.SaveChanges();

        var update = new UpdateCanvasArtifactCommandHandler(db);
        var second = await update.Handle(Update(rootId, content: "v2"), CancellationToken.None);
        var third = await update.Handle(Update(rootId, content: "v3", title: "Final"), CancellationToken.None);

        Assert.Equal(2, second!.Version);
        Assert.Equal(3, third!.Version);

        var read = new GetCanvasArtifactQueryHandler(db);
        var original = await read.Handle(new GetCanvasArtifactQuery { TenantId = TenantId, UserId = UserId, RootId = rootId, Version = 1 }, CancellationToken.None);
        var titleCarry = await read.Handle(new GetCanvasArtifactQuery { TenantId = TenantId, UserId = UserId, RootId = rootId, Version = 2 }, CancellationToken.None);
        var latest = await read.Handle(new GetCanvasArtifactQuery { TenantId = TenantId, UserId = UserId, RootId = rootId }, CancellationToken.None);

        // v1 untouched and still readable.
        Assert.Equal("v1", original!.Content);
        Assert.Equal("Original", original.Title);
        // The title-less v2 save carried the previous title forward.
        Assert.Equal("Original", titleCarry!.Title);
        // Latest is v3 with the new title/content.
        Assert.Equal(3, latest!.Version);
        Assert.Equal("v3", latest.Content);
        Assert.Equal("Final", latest.Title);

        var versions = db.CanvasArtifacts.AsNoTracking()
            .Where(c => c.RootId == rootId)
            .Select(c => c.Version)
            .OrderBy(v => v)
            .ToList();
        Assert.Equal(new[] { 1, 2, 3 }, versions.ToArray());
    }
}
