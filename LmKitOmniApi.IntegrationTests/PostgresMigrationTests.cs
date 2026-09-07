using DotNet.Testcontainers.Containers;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LmKitOmniApi.IntegrationTests;

/// <summary>An empty PostgreSQL. The migrations under test are the only thing that seeds it.</summary>
public sealed class MigrationPostgresFixture : DatabaseContainerFixture
{
    protected override IContainer Build() =>
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override Task SeedAsync(string connectionString, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Applies EVERY migration to a REAL, empty PostgreSQL and checks what came out.
///
/// <para><b>Why this exists.</b> The whole test suite builds its schema with
/// <c>Database.EnsureCreated()</c>, which reads the current model and never opens the
/// migrations folder. So a migration could be missing, misordered, or actively destructive
/// and 1500+ tests would still pass — the only thing between a broken migration and
/// production was somebody reading the diff, and a scaffolded migration in this repository
/// that would have backdated every existing row to <c>-infinity</c> is how close that came to
/// failing.</para>
///
/// <para><b>Why here and not in the fast suite.</b> <c>LmKitOmniApi.Tests</c> already carries
/// <c>MigrationIntegrityTests</c>, which diffs the model against the snapshot and translates
/// every migration through the Npgsql SQL generator in about a second and with no database.
/// What it cannot do is find out whether PostgreSQL ACCEPTS the result — a unique index over
/// duplicate rows, a non-nullable column added to a populated table, DDL the server refuses
/// inside a transaction. That needs a server, and it has to be PostgreSQL: SQLite has no
/// <c>ALTER COLUMN</c>, no <c>jsonb</c>, and no equivalent for most of what these migrations
/// do, so a SQLite migration test would prove something no environment ever runs.</para>
///
/// <para>Nothing below hard-codes a migration count or the name of the head migration.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresMigrationTests : IClassFixture<MigrationPostgresFixture>
{
    private readonly MigrationPostgresFixture _fixture;

    public PostgresMigrationTests(MigrationPostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// One container, but a brand-new DATABASE per test.
    ///
    /// <para>The tests below share a class fixture, so they share a container, and xUnit does
    /// not promise the order it runs them in. Pointing them all at the container's default
    /// database made "apply every migration to an EMPTY database" depend on being scheduled
    /// first — it was not, another test had already migrated, and the assertion failed for a
    /// reason that had nothing to do with the migrations. Creating the database here makes
    /// each test's starting state its own, at the cost of a <c>CREATE DATABASE</c>, while the
    /// expensive part (the container) stays shared.</para>
    /// </summary>
    private async Task<HermesDbContext> CreateFreshDatabaseContextAsync()
    {
        var name = $"migrations_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            // CREATE DATABASE cannot run inside a transaction, hence the raw command. The name
            // is a generated GUID, not caller input.
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = name
        }.ConnectionString;

        return new HermesDbContext(
            new DbContextOptionsBuilder<HermesDbContext>().UseNpgsql(connectionString).Options);
    }

    /// <summary>
    /// The headline check: migrate an empty database to head, then assert that the schema the
    /// migrations produced is the schema the model describes. A green
    /// <c>EnsureCreated()</c> suite plus a red assertion here is exactly the class of defect
    /// nothing else in this repository can see.
    /// </summary>
    [SkippableFact]
    public async Task EveryMigration_AppliesToAnEmptyDatabaseAndReproducesTheModel()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        await using var context = await CreateFreshDatabaseContextAsync();
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        Assert.NotEmpty(pending);

        await context.Database.MigrateAsync();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(pending, applied);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "Applying every migration to an empty PostgreSQL produced a schema that does not "
            + "match the EF model. The migrations and the model have diverged, so production "
            + "(which migrates) and the test suite (which calls EnsureCreated) are running "
            + "different schemas.");
    }

    /// <summary>
    /// Migrating is not the same as migrating a database that has rows in it. Applying the
    /// history a second time on top of an already-migrated database is what a redeploy does,
    /// and it must be a no-op rather than an error.
    /// </summary>
    [SkippableFact]
    public async Task MigratingAnAlreadyMigratedDatabase_IsANoOp()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        await using var context = await CreateFreshDatabaseContextAsync();
        await context.Database.MigrateAsync();
        var afterFirst = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        await context.Database.MigrateAsync();

        Assert.Equal(afterFirst, await context.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>
    /// The migrated schema has to be usable, not merely creatable: a round-trip through the
    /// real provider catches column types the model and the migration disagree about — the
    /// kind of mismatch <c>EnsureCreated()</c> cannot produce because it emits both sides.
    /// </summary>
    [SkippableFact]
    public async Task TheMigratedSchema_AcceptsAndReturnsARow()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        await using var context = await CreateFreshDatabaseContextAsync();
        await context.Database.MigrateAsync();

        var tenantId = Guid.NewGuid();
        context.Tenants.Add(new LmKitOmniApi.Domain.Entities.Tenant
        {
            Id = tenantId,
            Name = "Migration round-trip"
        });
        context.Users.Add(new LmKitOmniApi.Domain.Entities.User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = "migrated",
            Email = "migrated@example.test",
            FullName = "Migrated User",
            PasswordHash = "not-a-real-hash"
        });
        await context.SaveChangesAsync();

        var readBack = await context.Users.AsNoTracking()
            .SingleAsync(user => user.Email == "migrated@example.test");
        Assert.Equal(tenantId, readBack.TenantId);
        Assert.True(readBack.IsActive);
        Assert.Equal(0, readBack.FailedLoginAttempts);
    }

    /// <summary>
    /// A DBA-gated deployment does not run <c>Migrate()</c>; it runs the idempotent SQL
    /// script, which comes out of a different code path — every operation wrapped in its own
    /// existence check. Running that script against a database ALREADY at head is the
    /// strongest cheap proof those guards work: if any of them is wrong, the server rejects
    /// the statement (duplicate table, duplicate column, duplicate index) instead of skipping
    /// it, and a production deployment would have failed the same way.
    /// </summary>
    [SkippableFact]
    public async Task TheIdempotentMigrationScript_IsSafeToRunAgainstADatabaseAlreadyAtHead()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        await using var context = await CreateFreshDatabaseContextAsync();
        await context.Database.MigrateAsync();

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: null,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.False(string.IsNullOrWhiteSpace(script));

        await context.Database.ExecuteSqlRawAsync(script);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
}
