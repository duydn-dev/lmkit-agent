using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Indexes an external database's schema into a PER-CONNECTION Qdrant collection
/// (physical tenant isolation, like the per-tenant agent-memory collections) and
/// retrieves the tables most relevant to a natural-language request for SQL
/// generation. Re-index clears the connection's points first (delete-before-
/// reingest) so dropped tables/columns never linger.
/// </summary>
public sealed class SchemaIndexingService
{
    private const string ConnectionIdField = "ConnectionId";

    private readonly ExternalDatabaseService _databases;
    private readonly ISchemaEmbedder _embedder;
    private readonly IVectorStoreService _vectorStore;
    private readonly ILogger<SchemaIndexingService> _logger;

    public SchemaIndexingService(
        ExternalDatabaseService databases,
        ISchemaEmbedder embedder,
        IVectorStoreService vectorStore,
        ILogger<SchemaIndexingService> logger)
    {
        _databases = databases;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public static string CollectionName(Guid tenantId, Guid connectionId) =>
        $"db_schema_{tenantId:N}_{connectionId:N}";

    /// <summary>Introspects, (re)builds the connection's schema index, and returns the table count indexed.</summary>
    public async Task<int> IndexAsync(DbProvider provider, string connectionString, Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        var tables = await _databases.IntrospectAsync(provider, connectionString, ct);
        var collection = CollectionName(tenantId, connectionId);

        var cards = tables.Select(t => (t, card: SchemaCardBuilder.Build(t))).ToList();
        if (cards.Count == 0)
        {
            _logger.LogInformation("🗄️ Schema index: connection {ConnectionId} has no tables to index.", connectionId);
            return 0;
        }

        // Embed the first card to learn the vector size, ensure the collection, then
        // clear this connection's prior points before re-ingesting.
        var firstVector = await _embedder.EmbedAsync(cards[0].card, ct);
        await _vectorStore.EnsureCollectionExistsAsync(collection, (ulong)firstVector.Length, ct);
        await _vectorStore.DeleteByPayloadFilterAsync(collection, ConnectionIdField, connectionId.ToString(), ct);

        for (var i = 0; i < cards.Count; i++)
        {
            var (table, card) = cards[i];
            var vector = i == 0 ? firstVector : await _embedder.EmbedAsync(card, ct);
            var qualified = string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
            var payload = new Dictionary<string, object>
            {
                { "TenantId", tenantId.ToString() },
                { ConnectionIdField, connectionId.ToString() },
                { "Table", qualified },
                { "Card", card }
            };
            await _vectorStore.UpsertVectorAsync(collection, Guid.NewGuid(), vector, payload, ct);
        }

        _logger.LogInformation("🗄️ Schema index: {Count} tables indexed for connection {ConnectionId}.", cards.Count, connectionId);
        return cards.Count;
    }

    /// <summary>Returns the top-K relevant table cards for a request, joined as an SQL-generation context block (empty string when none).</summary>
    public async Task<string> RetrieveContextAsync(Guid tenantId, Guid connectionId, string nlQuery, int topK, CancellationToken ct)
    {
        var collection = CollectionName(tenantId, connectionId);
        var queryVector = await _embedder.EmbedAsync(nlQuery, ct);
        var results = await _vectorStore.SearchSimilarWithAnyPayloadAsync(
            collection, queryVector, ConnectionIdField, new[] { connectionId.ToString() }, topK, ct);

        var cards = results
            .Select(r => r.Payload.TryGetValue("Card", out var card) ? card : null)
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .ToList();

        return cards.Count == 0 ? string.Empty : string.Join("\n\n", cards);
    }
}
