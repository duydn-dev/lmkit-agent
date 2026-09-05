using DotNet.Testcontainers.Containers;
using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace LmKitOmniApi.IntegrationTests;

public sealed class MongoFixture : DatabaseContainerFixture
{
    public const string DatabaseName = "lmkit_it";
    public const string Collection = "orders";
    public const int SeededCount = 4;

    protected override IContainer Build() =>
        new MongoDbBuilder().WithImage("mongo:7").Build();

    // MongoDbContainer does NOT implement IDatabaseContainer (unlike the SQL modules), so
    // the base cast would fail — resolve via the concrete container's own GetConnectionString().
    protected override string ResolveConnectionString(IContainer container) =>
        ((MongoDbContainer)container).GetConnectionString();

    protected override async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        var client = new MongoClient(connectionString);
        var collection = client.GetDatabase(DatabaseName).GetCollection<BsonDocument>(Collection);
        await collection.InsertManyAsync(new[]
        {
            new BsonDocument { { "status", "paid" }, { "total", 100 } },
            new BsonDocument { { "status", "paid" }, { "total", 250 } },
            new BsonDocument { { "status", "pending" }, { "total", 40 } },
            new BsonDocument { { "status", "refunded" }, { "total", 10 } },
        }, cancellationToken: ct);
    }
}

/// <summary>
/// Live MongoDB proof (GAP 2), opt-in via Testcontainers. Skips when Docker is absent.
///
/// MongoDB has no server-side read-only transaction, so the read-path gate is the
/// deterministic <see cref="MongoCommandClassifier"/> plus the least-privilege account —
/// proven here (a). Also, <see cref="MongoDatabaseService"/> egress-vets every call and a
/// local container is only reachable on a loopback/private address the SSRF guard blocks,
/// so the backup (b) and schema-sampling (c) MECHANICS the service performs are exercised
/// against the real container through the same driver the service uses, and the service's
/// live egress refusal is asserted directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MongoIntegrationTests : IClassFixture<MongoFixture>
{
    private readonly MongoFixture _fixture;

    public MongoIntegrationTests(MongoFixture fixture) => _fixture = fixture;

    private IMongoCollection<BsonDocument> Orders()
    {
        var client = new MongoClient(_fixture.ConnectionString);
        return client.GetDatabase(MongoFixture.DatabaseName).GetCollection<BsonDocument>(MongoFixture.Collection);
    }

    [SkippableFact]
    public async Task Backup_BeforeWrite_MakesRealCopyOfCollection()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var orders = Orders();
        var before = await orders.CountDocumentsAsync(new BsonDocument());
        Assert.Equal(MongoFixture.SeededCount, before);

        // The exact backup mechanic MongoDatabaseService.RunWriteApprovedAsync runs before a write.
        var backupName = $"lmkit_backup_orders_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var pipeline = new[] { new BsonDocument("$match", new BsonDocument()), new BsonDocument("$out", backupName) };
        await orders.AggregateAsync<BsonDocument>(pipeline);

        // Then the write happens — the backup, taken first, is a full independent copy.
        await orders.DeleteOneAsync(new BsonDocument("status", "refunded"));

        var db = new MongoClient(_fixture.ConnectionString).GetDatabase(MongoFixture.DatabaseName);
        var backupCount = await db.GetCollection<BsonDocument>(backupName).CountDocumentsAsync(new BsonDocument());
        var afterWrite = await orders.CountDocumentsAsync(new BsonDocument());

        Assert.Equal(before, backupCount);          // backup captured every pre-write document
        Assert.Equal(before - 1, afterWrite);        // and the write really applied
    }

    [SkippableFact]
    public async Task SchemaSampling_ListsSeededCollectionAndFields()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var db = new MongoClient(_fixture.ConnectionString).GetDatabase(MongoFixture.DatabaseName);

        // The mechanic GetSchemaAsync uses: list collection names, then sample documents.
        var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
        Assert.Contains(MongoFixture.Collection, names);

        var sample = await db.GetCollection<BsonDocument>(MongoFixture.Collection).Find(new BsonDocument()).Limit(25).ToListAsync();
        Assert.NotEmpty(sample);
        Assert.Contains(sample, d => d.Contains("status") && d.Contains("total"));
    }

    [SkippableFact]
    public async Task ReadPathGate_And_EgressGuard_AreLive()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        // (a) Read-path gate (no server read-only in Mongo → classifier is the gate):
        // a write command is kept off the read path, and $out (write-from-read) is refused.
        Assert.Equal(MongoCommandKind.Write,
            MongoCommandClassifier.Classify("{\"collection\":\"orders\",\"op\":\"deleteMany\",\"filter\":{}}").Kind);
        Assert.Equal(MongoCommandKind.Refused,
            MongoCommandClassifier.Classify("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$out\":\"x\"}]}").Kind);

        // The live SSRF egress guard refuses the service even connecting to the loopback
        // container (defense-in-depth beyond the least-privilege account).
        var service = new MongoDatabaseService(
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions()),
            NullLogger<MongoDatabaseService>.Instance);

        var denial = await service.TestConnectionAsync(_fixture.ConnectionString, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(denial));
        Assert.Contains("nội bộ", denial); // "internal address blocked"
    }
}
