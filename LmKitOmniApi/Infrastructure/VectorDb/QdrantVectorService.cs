using Qdrant.Client;
using Qdrant.Client.Grpc;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Infrastructure.VectorDb;

public class QdrantVectorService : IVectorStoreService
{
    private readonly QdrantClient _client;

    public QdrantVectorService(IConfiguration configuration)
    {
        var baseUrl = configuration["VectorStore:BaseUrl"] ?? "http://localhost:6334";
        var uri = new Uri(baseUrl);
        _client = new QdrantClient(uri.Host, uri.Port);
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
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords,
        string tenantFilterField, string tenantId, int topK, CancellationToken ct = default)
    {
        var results = new List<VectorSearchResult>();
        if (keywords.Count == 0) return results;

        try
        {
            // Build Qdrant filter: TenantId must match AND (Keywords contains any of the search keywords)
            var keywordConditions = keywords.Select(kw =>
                new Condition { Field = new FieldCondition
                {
                    Key = payloadField,
                    Match = new Match { Text = kw }
                }}
            ).ToList();

            var filter = new Filter
            {
                Must =
                {
                    // Tenant filter
                    new Condition { Field = new FieldCondition
                    {
                        Key = tenantFilterField,
                        Match = new Match { Keyword = tenantId }
                    }},
                },
                Should = { keywordConditions } // Any keyword match
            };

            // Use Scroll for filter-only search (no vector needed)
            var scrollResult = await _client.ScrollAsync(
                collectionName: collectionName,
                filter: filter,
                limit: (uint)topK,
                payloadSelector: true,
                cancellationToken: ct
            );

            foreach (var p in scrollResult.Result)
            {
                var payload = MapPayload(p.Payload);

                // Score based on number of keyword matches in the payload content
                var contentText = payload.ContainsKey("Content") ? payload["Content"].ToLowerInvariant() : "";
                var kwText = payload.ContainsKey("Keywords") ? payload["Keywords"].ToLowerInvariant() : "";
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Qdrant filter may fail if payload index not created — fall back gracefully
        }

        return results;
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
        var results = new List<VectorSearchResult>();
        if (keywords.Count == 0 || string.IsNullOrEmpty(tenantId) || documentIds.Count == 0)
            return results;

        try
        {
            var keywordConditions = keywords.Select(kw =>
                new Condition { Field = new FieldCondition
                {
                    Key = payloadField,
                    Match = new Match { Text = kw }
                }}
            ).ToList();

            // Document allowlist as an OR-group (any pinned DocumentId matches).
            var documentAllowlist = AnyKeywordMatch(documentIdField, documentIds);

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
                    // Pinned-knowledge document allowlist
                    new Condition { Filter = documentAllowlist }
                },
                Should = { keywordConditions } // Any keyword match
            };

            var scrollResult = await _client.ScrollAsync(
                collectionName: collectionName,
                filter: filter,
                limit: (uint)topK,
                payloadSelector: true,
                cancellationToken: ct
            );

            foreach (var p in scrollResult.Result)
            {
                var payload = MapPayload(p.Payload);

                // Score based on number of keyword matches in the payload content
                var contentText = payload.ContainsKey("Content") ? payload["Content"].ToLowerInvariant() : "";
                var kwText = payload.ContainsKey("Keywords") ? payload["Keywords"].ToLowerInvariant() : "";
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Qdrant filter may fail if payload index not created — fall back gracefully
        }

        return results;
    }

    // --- Shared search-result mapping helpers ------------------------------------

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
}
