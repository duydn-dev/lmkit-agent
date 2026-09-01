using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pha 1: schema-card building + the introspect→embed→upsert index pipeline and
/// top-K retrieval. Runs end-to-end in CI against a real temp SQLite file with a
/// fake embedder (no model) and an in-memory vector store (no Qdrant), so the
/// wiring — per-connection collection, delete-before-reingest, payloads,
/// retrieval — is fully exercised.
/// </summary>
public sealed class SchemaIndexingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SchemaIndexingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lmkit-schema-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), total REAL);
            """;
        command.ExecuteNonQuery();
    }

    // ── Card builder (pure) ──
    [Fact]
    public void CardBuilder_IncludesTableColumnsKeysAndFlags()
    {
        var table = new DbTableInfo("public", "customers", new[]
        {
            new DbColumnInfo("id", "integer", false, true),
            new DbColumnInfo("email", "text", true, false)
        }, new[] { "customer_id → customers.id" });

        var card = SchemaCardBuilder.Build(table);

        Assert.Contains("Table: public.customers", card);
        Assert.Contains("id integer PRIMARY KEY NOT NULL", card);
        Assert.Contains("email text", card);
        Assert.Contains("customer_id → customers.id", card);
    }

    // ── Index pipeline ──
    [Fact]
    public async Task IndexAsync_EnsuresCollection_ClearsOld_AndUpsertsOnePointPerTable()
    {
        var store = new FakeVectorStore();
        var service = CreateService(store);
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        var count = await service.IndexAsync(DbProvider.Sqlite, _connectionString, tenantId, connectionId, CancellationToken.None);

        Assert.Equal(2, count); // customers + orders
        var collection = SchemaIndexingService.CollectionName(tenantId, connectionId);
        Assert.Equal(collection, store.EnsuredCollection);
        Assert.True(store.DeleteByFilterCalled, "Must clear the connection's prior points before re-ingesting.");
        Assert.Equal(2, store.Points.Count);
        Assert.All(store.Points, p =>
        {
            Assert.Equal(connectionId.ToString(), p.Payload["ConnectionId"]);
            Assert.Equal(tenantId.ToString(), p.Payload["TenantId"]);
            Assert.Contains("Table:", p.Payload["Card"]);
        });
    }

    [Fact]
    public async Task Reindex_ReplacesPoints_RatherThanAppending()
    {
        var store = new FakeVectorStore();
        var service = CreateService(store);
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await service.IndexAsync(DbProvider.Sqlite, _connectionString, tenantId, connectionId, CancellationToken.None);
        await service.IndexAsync(DbProvider.Sqlite, _connectionString, tenantId, connectionId, CancellationToken.None);

        // Delete-before-reingest means the second run cleared the first, not doubled.
        Assert.Equal(2, store.Points.Count);
    }

    [Fact]
    public async Task RetrieveContext_ReturnsIndexedCards()
    {
        var store = new FakeVectorStore();
        var service = CreateService(store);
        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await service.IndexAsync(DbProvider.Sqlite, _connectionString, tenantId, connectionId, CancellationToken.None);
        var context = await service.RetrieveContextAsync(tenantId, connectionId, "khách hàng và đơn hàng", 5, CancellationToken.None);

        Assert.Contains("Table: main.customers", context);
        Assert.Contains("Table: main.orders", context);
    }

    private static SchemaIndexingService CreateService(FakeVectorStore store)
    {
        var databases = new ExternalDatabaseService(
            new IExternalDatabaseProvider[] { new SqliteDatabaseProvider() },
            new DbEgressValidator(Options.Create(new DatabaseAgentOptions())),
            Options.Create(new DatabaseAgentOptions()));
        return new SchemaIndexingService(databases, new FakeEmbedder(), store, NullLogger<SchemaIndexingService>.Instance);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    // Deterministic 8-dim embedding from the text bytes — same text → same vector.
    private sealed class FakeEmbedder : ISchemaEmbedder
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var vector = new float[8];
            var bytes = Encoding.UTF8.GetBytes(text);
            for (var i = 0; i < bytes.Length; i++) vector[i % 8] += bytes[i];
            return Task.FromResult(vector);
        }
    }

    private sealed class StoredPoint
    {
        public Guid Id { get; init; }
        public Dictionary<string, string> Payload { get; init; } = new();
    }

    private sealed class FakeVectorStore : IVectorStoreService
    {
        public List<StoredPoint> Points { get; } = new();
        public string? EnsuredCollection { get; private set; }
        public bool DeleteByFilterCalled { get; private set; }

        public Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize, CancellationToken ct = default)
        {
            EnsuredCollection = collectionName;
            return Task.CompletedTask;
        }

        public Task UpsertVectorAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object>? payload = null, CancellationToken ct = default)
        {
            Points.Add(new StoredPoint
            {
                Id = id,
                Payload = payload?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty) ?? new()
            });
            return Task.CompletedTask;
        }

        public Task DeleteByPayloadFilterAsync(string collectionName, string payloadField, string value, CancellationToken ct = default)
        {
            DeleteByFilterCalled = true;
            Points.RemoveAll(p => p.Payload.TryGetValue(payloadField, out var v) && v == value);
            return Task.CompletedTask;
        }

        public Task<List<VectorSearchResult>> SearchSimilarWithAnyPayloadAsync(
            string collectionName, float[] queryVector, string payloadField, IReadOnlyList<string> allowedValues, int topK, CancellationToken ct = default)
        {
            var matches = Points
                .Where(p => p.Payload.TryGetValue(payloadField, out var v) && allowedValues.Contains(v))
                .Take(topK)
                .Select(p => new VectorSearchResult { Id = p.Id, Score = 1f, Payload = p.Payload })
                .ToList();
            return Task.FromResult(matches);
        }

        public Task DeleteVectorsAsync(string collectionName, IReadOnlyList<Guid> ids, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(string collectionName, string payloadField, List<string> keywords, string tenantFilterField, string tenantId, int topK, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<VectorSearchResult>> SearchSimilarWithinDocumentsAsync(string collectionName, float[] queryVector, string tenantField, string tenantId, string documentIdField, IReadOnlyList<string> documentIds, int topK, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<VectorSearchResult>> SearchByPayloadWithinDocumentsAsync(string collectionName, string payloadField, List<string> keywords, string tenantField, string tenantId, string documentIdField, IReadOnlyList<string> documentIds, int topK, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
