using System.Collections.Concurrent;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Infrastructure.VectorDb;

public class QdrantVectorService : IVectorStoreService, IDisposable
{
    /// <summary>
    /// Payload field carrying the extracted keywords of a chunk. Sparse (keyword)
    /// retrieval matches it with Qdrant's <c>Match.Text</c>, which REQUIRES a
    /// full-text payload index — without one Qdrant rejects the query outright.
    /// </summary>
    internal const string FullTextIndexField = "Keywords";

    /// <summary>
    /// Payload fields used as exact-match filters (tenant/scope/document scoping).
    /// Keyword indexes keep those filters from degrading into full scans.
    /// </summary>
    internal static readonly string[] KeywordIndexFields = ["AccessScope", "TenantId", "DocumentId"];

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorService> _logger;

    /// <summary>Collections whose payload indexes this process has already ensured.</summary>
    private readonly ConcurrentDictionary<string, bool> _indexedCollections = new(StringComparer.Ordinal);

    private bool _disposed;

    public QdrantVectorService(IConfiguration configuration, ILogger<QdrantVectorService> logger)
    {
        _logger = logger;
        // Honors https:// and VectorStore:ApiKey — see QdrantClientFactory.
        _client = QdrantClientFactory.Create(configuration);
    }

    public async Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize, CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (!collections.Contains(collectionName))
        {
            await _client.CreateCollectionAsync(
                collectionName: collectionName,
                vectorsConfig: new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
                cancellationToken: ct
            );
        }

        await EnsurePayloadIndexesAsync(collectionName, ct);
    }

    /// <summary>
    /// Creates the payload indexes the hybrid (dense + sparse) retrieval path
    /// depends on. Without the full-text index on <see cref="FullTextIndexField"/>
    /// every <c>Match.Text</c> filter fails, and hybrid search silently degrades
    /// to dense-only — which is exactly what used to happen, because nothing in
    /// the codebase ever created an index and the failure was swallowed.
    /// <para>
    /// Index creation is idempotent on the Qdrant side and best-effort here: a
    /// failure is logged at Warning and never blocks collection creation (an
    /// older server, or a read-only API key, must not break ingestion).
    /// </para>
    /// </summary>
    private async Task EnsurePayloadIndexesAsync(string collectionName, CancellationToken ct)
    {
        if (!_indexedCollections.TryAdd(collectionName, true)) return;

        try
        {
            await _client.CreatePayloadIndexAsync(
                collectionName: collectionName,
                fieldName: FullTextIndexField,
                schemaType: PayloadSchemaType.Text,
                indexParams: new PayloadIndexParams
                {
                    TextIndexParams = new TextIndexParams
                    {
                        Tokenizer = TokenizerType.Word,
                        Lowercase = true,
                        MinTokenLen = 2,
                        MaxTokenLen = 30
                    }
                },
                cancellationToken: ct);

            foreach (var field in KeywordIndexFields)
            {
                await _client.CreatePayloadIndexAsync(
                    collectionName: collectionName,
                    fieldName: field,
                    schemaType: PayloadSchemaType.Keyword,
                    cancellationToken: ct);
            }

            _logger.LogInformation(
                "Qdrant payload indexes ensured on {Collection}: full-text '{TextField}' + keyword {KeywordFields}.",
                collectionName, FullTextIndexField, KeywordIndexFields);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _indexedCollections.TryRemove(collectionName, out _);
            throw;
        }
        catch (Exception ex)
        {
            _indexedCollections.TryRemove(collectionName, out _);
            _logger.LogWarning(ex,
                "Failed to create Qdrant payload indexes on {Collection}; keyword (sparse) search will fail and "
                + "retrieval will fall back to dense-only.", collectionName);
        }
    }

    public async Task UpsertVectorAsync(
        string collectionName,
        Guid id,
        float[] vector,
        Dictionary<string, object>? payload = null,
        CancellationToken ct = default)
    {
        var pointId = new PointId { Uuid = id.ToString() };
        
        var point = new PointStruct
        {
            Id = pointId,
            Vectors = vector
        };

        if (payload != null)
        {
            foreach (var kvp in payload)
            {
                if (kvp.Value is string s) point.Payload.Add(kvp.Key, s);
                else if (kvp.Value is int i) point.Payload.Add(kvp.Key, i);
                else if (kvp.Value is float f) point.Payload.Add(kvp.Key, f);
                else if (kvp.Value is double d) point.Payload.Add(kvp.Key, d);
                else if (kvp.Value is bool b) point.Payload.Add(kvp.Key, b);
                else if (kvp.Value is not null) point.Payload.Add(kvp.Key, kvp.Value.ToString()!);
            }
        }

        var points = new List<PointStruct> { point };

        await _client.UpsertAsync(collectionName, points, cancellationToken: ct);
    }

    public async Task DeleteVectorsAsync(
        string collectionName,
        IReadOnlyList<Guid> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await _client.DeleteAsync(collectionName, ids, cancellationToken: ct);
    }

    public async Task DeleteByPayloadFilterAsync(
        string collectionName, string payloadField, string value, CancellationToken ct = default)
    {
        // Delete every point whose payload field equals value (e.g. all schema
        // points for one database connection) — used to clear stale schema before
        // a re-index of a per-connection collection.
        await _client.DeleteAsync(collectionName, AnyKeywordMatch(payloadField, new[] { value }), cancellationToken: ct);
    }

    public async Task<List<VectorSearchResult>> SearchSimilarWithAnyPayloadAsync(
        string collectionName,
        float[] queryVector,
        string payloadField,
        IReadOnlyList<string> allowedValues,
        int topK,
        CancellationToken ct = default)
    {
        if (allowedValues.Count == 0) return new List<VectorSearchResult>();

        var filter = AnyKeywordMatch(payloadField, allowedValues);

        var queryResult = await _client.QueryAsync(
            collectionName: collectionName,
            query: queryVector,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: ct);

        return MapResults(queryResult);
    }

    /// <summary>
    /// Dense search with a compound filter: (tenant matches) AND
    /// (documentId IN documentIds) — the custom-agent knowledge-pinning path.
    /// The tenant clause and the document allowlist are each an OR-group of
    /// keyword matches; the two groups are ANDed via nested filters under
    /// <c>Must</c> — Qdrant combines clauses of one filter with AND, so a single
    /// top-level <c>Should</c> could not express both at once. Scoping by TENANT
    /// (not the caller's private access scope) lets a shared agent's pinned
    /// documents, which may be owned by another member of the same tenant, be
    /// retrieved. Callers without a document restriction use
    /// <see cref="SearchSimilarWithAnyPayloadAsync"/> (private access scope).
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchSimilarWithinDocumentsAsync(
        string collectionName,
        float[] queryVector,
        string tenantField,
        string tenantId,
        string documentIdField,
        IReadOnlyList<string> documentIds,
        int topK,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId) || documentIds.Count == 0)
            return new List<VectorSearchResult>();

        var filter = new Filter
        {
            Must =
            {
                new Condition { Filter = AnyKeywordMatch(tenantField, new[] { tenantId }) },
                new Condition { Filter = AnyKeywordMatch(documentIdField, documentIds) }
            }
        };

        var queryResult = await _client.QueryAsync(
            collectionName: collectionName,
            query: queryVector,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: ct);

        return MapResults(queryResult);
    }

    /// <summary>
    /// H3 Fix: True payload-based keyword search using Qdrant's Scroll API with filters.
    /// Independent of vector similarity — finds documents by keyword presence.
    /// <para>
    /// Requires the full-text payload index created by
    /// <see cref="EnsureCollectionExistsAsync"/>. A Qdrant failure is logged at
    /// Warning and RETHROWN so the caller's documented dense fallback
    /// (<c>RagPipelineService.PerformKeywordSearchFallbackAsync</c>) actually
    /// runs — the old bare <c>catch (Exception) { }</c> both hid the failure and
    /// made that fallback unreachable.
    /// </para>
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords,
        string tenantFilterField, string tenantId, int topK, CancellationToken ct = default)
    {
        if (keywords.Count == 0) return new List<VectorSearchResult>();

        // Build Qdrant filter: tenant/scope must match AND (payload field contains any keyword)
        var filter = new Filter
        {
            Must =
            {
                new Condition { Field = new FieldCondition
                {
                    Key = tenantFilterField,
                    Match = new Match { Keyword = tenantId }
                }},
            },
            Should = { AnyTextMatch(payloadField, keywords) } // Any keyword match
        };

        return await ScrollAndScoreAsync(collectionName, filter, keywords, topK, nameof(SearchByPayloadFilterAsync), ct);
    }

    /// <summary>
    /// Tenant + document-scoped sparse (keyword) search for the pinned-knowledge
    /// path. Compound filter: (tenant matches) AND (documentId IN documentIds)
    /// AND (any keyword present). The tenant match and the document allowlist go
    /// in <c>Must</c> (the allowlist as a nested Should-group, mirroring the
    /// dense pinned path), leaving the top-level <c>Should</c> for the keyword
    /// OR-conditions. Tenant-scoped — NOT the caller's private access scope — so
    /// a shared agent's pinned docs owned by another tenant member are reachable.
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchByPayloadWithinDocumentsAsync(
        string collectionName,
        string payloadField,
        List<string> keywords,
        string tenantField,
        string tenantId,
        string documentIdField,
        IReadOnlyList<string> documentIds,
        int topK,
        CancellationToken ct = default)
    {
        if (keywords.Count == 0 || string.IsNullOrEmpty(tenantId) || documentIds.Count == 0)
            return new List<VectorSearchResult>();

        var filter = new Filter
        {
            Must =
            {
                // Tenant filter (tenant-wide, NOT the caller's private scope)
                new Condition { Field = new FieldCondition
                {
                    Key = tenantField,
                    Match = new Match { Keyword = tenantId }
                }},
                // Pinned-knowledge document allowlist — an OR-group (any pinned DocumentId matches).
                new Condition { Filter = AnyKeywordMatch(documentIdField, documentIds) }
            },
            Should = { AnyTextMatch(payloadField, keywords) } // Any keyword match
        };

        return await ScrollAndScoreAsync(collectionName, filter, keywords, topK, nameof(SearchByPayloadWithinDocumentsAsync), ct);
    }

    // --- Shared search-result mapping helpers ------------------------------------

    /// <summary>
    /// Runs a filter-only Scroll query and scores each hit by how many of the
    /// requested keywords appear in its Content/Keywords payload. Shared by both
    /// sparse-retrieval entry points.
    /// <para>
    /// A Qdrant failure — most commonly "Index required but not found" when the
    /// full-text payload index is missing — is logged at Warning WITH context and
    /// rethrown, so the caller's dense fallback is actually reached.
    /// </para>
    /// </summary>
    private async Task<List<VectorSearchResult>> ScrollAndScoreAsync(
        string collectionName, Filter filter, List<string> keywords, int topK, string operation, CancellationToken ct)
    {
        ScrollResponse scrollResult;
        try
        {
            scrollResult = await _client.ScrollAsync(
                collectionName: collectionName,
                filter: filter,
                limit: (uint)topK,
                payloadSelector: true,
                cancellationToken: ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Qdrant sparse search {Operation} failed on collection {Collection} with {KeywordCount} keyword(s). "
                + "A missing full-text payload index on '{TextField}' is the usual cause; the caller's dense fallback "
                + "will run.", operation, collectionName, keywords.Count, FullTextIndexField);
            throw;
        }

        var results = new List<VectorSearchResult>();
        foreach (var p in scrollResult.Result)
        {
            var payload = MapPayload(p.Payload);

            // Score based on number of keyword matches in the payload content
            var contentText = payload.TryGetValue("Content", out var content) ? content.ToLowerInvariant() : "";
            var kwText = payload.TryGetValue(FullTextIndexField, out var kws) ? kws.ToLowerInvariant() : "";
            var searchable = contentText + " " + kwText;
            var matchCount = keywords.Count(k => searchable.Contains(k.ToLowerInvariant()));
            var score = (float)matchCount / keywords.Count;

            results.Add(new VectorSearchResult
            {
                Id = Guid.Parse(p.Id.Uuid),
                Score = score,
                Payload = payload
            });
        }
        return results;
    }

    /// <summary>
    /// Builds an OR-group of full-text (<c>Match.Text</c>) conditions on one
    /// payload field. Needs the full-text index created by
    /// <see cref="EnsurePayloadIndexesAsync"/>.
    /// </summary>
    private static List<Condition> AnyTextMatch(string field, IReadOnlyList<string> keywords) =>
        keywords.Select(kw => new Condition
        {
            Field = new FieldCondition
            {
                Key = field,
                Match = new Match { Text = kw }
            }
        }).ToList();

    /// <summary>
    /// Projects a Qdrant point payload into the flat string dictionary used by
    /// <see cref="VectorSearchResult.Payload"/>. String and integer values keep
    /// their scalar rendering; every other kind falls back to the protobuf string
    /// form. Identical across the dense and scroll search paths.
    /// </summary>
    private static Dictionary<string, string> MapPayload(IReadOnlyDictionary<string, Value> payload) =>
        payload.ToDictionary(
            item => item.Key,
            item => item.Value.KindCase switch
            {
                Value.KindOneofCase.StringValue => item.Value.StringValue,
                Value.KindOneofCase.IntegerValue => item.Value.IntegerValue.ToString(),
                _ => item.Value.ToString()
            });

    /// <summary>Maps dense (vector) query hits to results, preserving each hit's score.</summary>
    private static List<VectorSearchResult> MapResults(IReadOnlyList<ScoredPoint> points) =>
        points.Select(point => new VectorSearchResult
        {
            Id = Guid.Parse(point.Id.Uuid),
            Score = point.Score,
            Payload = MapPayload(point.Payload)
        }).ToList();

    /// <summary>
    /// Builds an OR-group filter (Qdrant <c>Should</c>) of keyword matches on a
    /// single field — "<paramref name="field"/> equals any of <paramref name="values"/>".
    /// Used both as a standalone filter and as a nested clause under <c>Must</c>.
    /// </summary>
    private static Filter AnyKeywordMatch(string field, IReadOnlyList<string> values)
    {
        var group = new Filter();
        group.Should.AddRange(values.Select(value => new Condition
        {
            Field = new FieldCondition
            {
                Key = field,
                Match = new Match { Keyword = value }
            }
        }));
        return group;
    }

    /// <summary>
    /// Releases the gRPC channel behind the client. Registered as a DI singleton,
    /// so the container disposes this at shutdown; previously the channel was
    /// simply leaked.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
