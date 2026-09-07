using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The migrations were, until this file existed, dead code as far as the suite was concerned.
///
/// <para><b>The hole.</b> Every test host builds its schema with
/// <c>Database.EnsureCreated()</c>, which reads the CURRENT model and emits DDL straight from
/// it — the migration folder is never opened. A migration that is syntactically broken, that
/// drops the wrong column, or that backdates every existing row therefore passes the entire
/// suite; the only thing standing between it and production was somebody reading the diff.
/// That is not hypothetical here: a scaffolded migration in this repository would have
/// stamped every existing row with <c>-infinity</c> and left a permanent column
/// <c>DEFAULT</c> behind, and a human reading it is the only reason it did not ship.</para>
///
/// <para><b>What this file covers, and what it deliberately does not.</b> These tests are
/// provider-accurate but connectionless: they build the model and generate SQL through the
/// REAL Npgsql provider (the one production runs on), which is enough to catch a migration
/// that cannot be translated to PostgreSQL at all, and a model that has drifted away from the
/// snapshot. They cost milliseconds and need no database, so the ~25s suite stays ~25s.
/// They cannot catch a migration that generates valid SQL the SERVER then rejects — a
/// non-nullable column added to a populated table, a unique index over duplicate rows, an
/// operation Postgres refuses inside a transaction. That needs a real server, so it lives in
/// <c>LmKitOmniApi.IntegrationTests/PostgresMigrationTests.cs</c>, which applies every
/// migration to a fresh containerised PostgreSQL and now runs in CI. SQLite was rejected as
/// the venue for that: it cannot express what these migrations do (it has no
/// <c>ALTER COLUMN</c>, no <c>jsonb</c>, no partial-index syntax compatible with the
/// generated DDL), so a SQLite "migration test" would prove something no environment runs.
/// </para>
///
/// <para>Nothing here hard-codes a migration count or the name of the head migration: the
/// checks are expressed over "whatever the migrations assembly contains", so adding a
/// migration cannot break them.</para>
/// </summary>
public sealed class MigrationIntegrityTests
{
    /// <summary>
    /// Never connected to. Npgsql builds its model, its migrations assembly and its SQL
    /// generator without opening a socket; a host is required only because the connection
    /// string must parse.
    /// </summary>
    private const string OfflineConnectionString =
        "Host=127.0.0.1;Port=5432;Database=lmkit_migration_integrity;Username=unused;Password=unused";

    private static HermesDbContext CreateProductionProviderContext() =>
        new(new DbContextOptionsBuilder<HermesDbContext>()
            .UseNpgsql(OfflineConnectionString)
            .Options);

    /// <summary>
    /// The programmatic equivalent of <c>dotnet ef migrations has-pending-model-changes</c>.
    ///
    /// <para>An entity change that nobody scaffolded a migration for is invisible to the rest
    /// of the suite — <c>EnsureCreated()</c> happily creates the new shape, every test passes,
    /// and the column simply does not exist in production. This is the check that turns that
    /// into a red build.</para>
    /// </summary>
    [Fact]
    public void ModelSnapshot_HasNoPendingChanges()
    {
        using var context = CreateProductionProviderContext();

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF model no longer matches Migrations/HermesDbContextModelSnapshot.cs, so the "
            + "schema change you just made would never reach a real database: every test host "
            + "builds its schema with EnsureCreated() from the model, and production applies "
            + "migrations. Scaffold one with `dotnet ef migrations add <Name> --project "
            + "LmKitOmniApi`, then READ the generated Up() before committing it.");
    }

    /// <summary>
    /// Proves <see cref="ModelSnapshot_HasNoPendingChanges"/> is ARMED rather than merely
    /// green.
    ///
    /// <para>A check that always passes is worse than no check, and this one has two ways to
    /// rot silently: <c>HasPendingModelChanges()</c> answering <see langword="false"/> because
    /// it found no snapshot to compare against, and the snapshot drifting to a different
    /// assembly. So this test injects ONE extra shadow property into the runtime model — the
    /// smallest possible schema drift — and demands that the check notices. If it does not,
    /// the guard above is decoration.</para>
    ///
    /// <para>The drift is injected through <see cref="IModelCustomizer"/> on a throwaway
    /// options object, so nothing about the real model, the snapshot, or any other test is
    /// touched.</para>
    /// </summary>
    [Fact]
    public void PendingModelChangeCheck_DetectsASingleAddedColumn()
    {
        using var drifted = new HermesDbContext(
            new DbContextOptionsBuilder<HermesDbContext>()
                .UseNpgsql(OfflineConnectionString)
                .ReplaceService<IModelCustomizer, DriftInjectingModelCustomizer>()
                .Options);

        Assert.True(
            drifted.Database.HasPendingModelChanges(),
            "A model carrying one extra column was reported as matching the migration snapshot. "
            + "The pending-model-change guard is no longer detecting schema drift, so "
            + $"{nameof(ModelSnapshot_HasNoPendingChanges)} proves nothing.");
    }

    /// <summary>Adds one shadow property to <c>User</c> so the differ has something to find.</summary>
    private sealed class DriftInjectingModelCustomizer(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<LmKitOmniApi.Domain.Entities.User>()
                .Property<string>("MigrationGuardCanary");
        }
    }

    /// <summary>
    /// Runs every migration's <c>Up()</c> through the production provider's SQL generator,
    /// from an empty database to head. No server is touched, but the whole operation list of
    /// every migration is built and translated, so a migration that cannot be expressed on
    /// PostgreSQL fails here instead of during a deployment.
    /// </summary>
    [Fact]
    public void EveryMigration_TranslatesToPostgresSqlFromAnEmptyDatabase()
    {
        using var context = CreateProductionProviderContext();
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: null,
            options: MigrationsSqlGenerationOptions.Default);

        Assert.False(string.IsNullOrWhiteSpace(script), "Generating the full migration script produced nothing.");
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);

        // Every migration must record itself in the history table; a migration whose Up()
        // produced no operations at all is a scaffolding accident worth surfacing.
        var migrations = context.GetService<IMigrationsAssembly>().Migrations;
        Assert.NotEmpty(migrations);
        foreach (var migrationId in migrations.Keys)
        {
            Assert.Contains(migrationId, script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The idempotent form is what a deployment that cannot assume the target's current
    /// version has to use, and it goes down a different code path in the SQL generator
    /// (every operation wrapped in an existence check). Generating it proves that path works
    /// for this history too.
    /// </summary>
    [Fact]
    public void EveryMigration_TranslatesToAnIdempotentPostgresScript()
    {
        using var context = CreateProductionProviderContext();

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: null,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.False(string.IsNullOrWhiteSpace(script), "Generating the idempotent migration script produced nothing.");
    }

    /// <summary>
    /// EF orders migrations by their string id, and applies them in that order. Two migrations
    /// scaffolded on two branches on the same day can collide or interleave wrongly once
    /// merged; the differ never notices because the merged snapshot is still consistent.
    /// </summary>
    [Fact]
    public void MigrationIds_AreUniqueAndSortInTheOrderEfWillApplyThem()
    {
        using var context = CreateProductionProviderContext();
        var ids = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal).ToList(), ids);
    }

    /// <summary>
    /// Guards the assumption the rest of this file rests on: the migrations really do live in
    /// the application assembly, and the model snapshot really is there to be compared
    /// against. If either ever moved, <see cref="ModelSnapshot_HasNoPendingChanges"/> would go
    /// green by comparing nothing at all.
    /// </summary>
    [Fact]
    public void MigrationsAssembly_ShipsWithTheApplicationAndCarriesASnapshot()
    {
        using var context = CreateProductionProviderContext();
        var assembly = context.GetService<IMigrationsAssembly>();

        Assert.NotNull(assembly.ModelSnapshot);
        Assert.Equal(typeof(HermesDbContext).Assembly, assembly.Assembly);
    }
}
