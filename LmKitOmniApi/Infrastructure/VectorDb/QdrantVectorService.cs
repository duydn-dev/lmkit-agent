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

    public async Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize)
    {
        var collections = await _client.ListCollectionsAsync();
        if (!collections.Contains(collectionName))
        {
            await _client.CreateCollectionAsync(
                collectionName: collectionName,
                vectorsConfig: new VectorParams { Size = vectorSize, Distance = Distance.Cosine }
            );
        }
    }

    public async Task UpsertVectorAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object>? payload = null)
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
                else if (kvp.Value != null) point.Payload.Add(kvp.Key, kvp.Value.ToString());
            }
        }

        var points = new List<PointStruct> { point };

        await _client.UpsertAsync(collectionName, points);
    }

    public async Task<List<VectorSearchResult>> SearchSimilarAsync(string collectionName, float[] queryVector, int topK)
    {
        var searchResult = await _client.SearchAsync(
            collectionName: collectionName,
            vector: queryVector,
            limit: (ulong)topK,
            payloadSelector: true
        );

        var results = new List<VectorSearchResult>();
        foreach(var p in searchResult)
        {
            var payload = new Dictionary<string, string>();
            foreach (var kvp in p.Payload)
            {
                if (kvp.Value.KindCase == Qdrant.Client.Grpc.Value.KindOneofCase.StringValue)
                    payload[kvp.Key] = kvp.Value.StringValue;
                else if (kvp.Value.KindCase == Qdrant.Client.Grpc.Value.KindOneofCase.IntegerValue)
                    payload[kvp.Key] = kvp.Value.IntegerValue.ToString();
                else
                    payload[kvp.Key] = kvp.Value.ToString();
            }

            results.Add(new VectorSearchResult
            {
                Id = Guid.Parse(p.Id.Uuid),
                Score = p.Score,
                Payload = payload
            });
        }
        return results;
    }

    /// <summary>
    /// H3 Fix: True payload-based keyword search using Qdrant's Scroll API with filters.
    /// Independent of vector similarity — finds documents by keyword presence.
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
        string collectionName, string payloadField, List<string> keywords,
        string tenantFilterField, string tenantId, int topK)
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
                payloadSelector: true
            );

            foreach (var p in scrollResult.Result)
            {
                var payload = new Dictionary<string, string>();
                foreach (var kvp in p.Payload)
                {
                    payload[kvp.Key] = kvp.Value.KindCase switch
                    {
                        Value.KindOneofCase.StringValue => kvp.Value.StringValue,
                        Value.KindOneofCase.IntegerValue => kvp.Value.IntegerValue.ToString(),
                        _ => kvp.Value.ToString()
                    };
                }

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
        catch (Exception)
        {
            // Qdrant filter may fail if payload index not created — fall back gracefully
        }

        return results;
    }
}
