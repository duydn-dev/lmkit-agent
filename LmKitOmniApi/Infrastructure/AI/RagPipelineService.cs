using LMKit.TextGeneration;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Enhanced RAG Pipeline with true Hybrid Search:
/// - Dense retrieval (vector similarity via embeddings)
/// - Sparse retrieval (keyword matching via BM25-like scoring)
/// - Query expansion (synonyms + multi-query)
/// - Cross-encoder reranking
/// - Reciprocal Rank Fusion (RRF) for merging results
/// 
/// Inspired by console_net/rag-and-knowledge/query-expansion + reranker.
/// </summary>
public class RagPipelineService : IRagPipelineService
{
    private readonly IVectorStoreService _vectorStore;
    private readonly ITextChunkingService _chunkingService;
    private readonly LmModelManager _modelManager;
    private readonly QueryExpansionService _queryExpansion;
    private readonly ILogger<RagPipelineService> _logger;
    private readonly string _collectionName;

    public RagPipelineService(
        IVectorStoreService vectorStore, 
        ITextChunkingService chunkingService, 
        LmModelManager modelManager,
        QueryExpansionService queryExpansion,
        ILogger<RagPipelineService> logger,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _vectorStore = vectorStore;
        _chunkingService = chunkingService;
        _modelManager = modelManager;
        _queryExpansion = queryExpansion;
        _logger = logger;
        _collectionName = configuration["VectorStore:CollectionName"] ?? "lmkit_chunks";
    }

    public async Task<string> IngestDocumentAsync(Guid tenantId, string fileName, string content)
    {
        var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
        await _vectorStore.EnsureCollectionExistsAsync(_collectionName, (ulong)embeddingModel.EmbeddingSize);

        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
        var chunks = _chunkingService.ChunkText(content);
        int totalChunks = 0;

        foreach (var chunk in chunks)
        {
            var vector = embedder.GetEmbeddings(chunk);
            
            // Extract keywords for sparse search support
            var keywords = _queryExpansion.ExtractKeywords(chunk);
            
            var payload = new Dictionary<string, object>
            {
                { "TenantId", tenantId.ToString() },
                { "FileName", fileName },
                { "Content", chunk },
                { "Keywords", string.Join(" ", keywords) } // Sparse search field
            };
            
            await _vectorStore.UpsertVectorAsync(_collectionName, Guid.NewGuid(), vector, payload);
            totalChunks++;
        }

        _logger.LogInformation("📄 Ingested {Chunks} chunks from '{File}' for tenant {Tenant}", 
            totalChunks, fileName, tenantId);
        return $"Ingested {totalChunks} chunks from {fileName}.";
    }

    public async Task<string> QueryKnowledgeBaseAsync(Guid tenantId, string query, int topK = 3)
    {
        _logger.LogInformation("🔍 Hybrid search starting for query: '{Query}'", 
            query.Length > 80 ? query.Substring(0, 80) + "..." : query);

        var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);

        // === Stage 1: Query Expansion ===
        var expandedQueries = await _queryExpansion.ExpandQueryAsync(query, maxExpansions: 2);
        _logger.LogInformation("🔍 Expanded to {Count} query variations", expandedQueries.Count);

        // === Stage 2: Dense Retrieval (Vector Search) across all expanded queries ===
        var allDenseResults = new List<(string Content, float Score, string Source)>();
        int initialTopK = topK * 5;

        foreach (var expandedQuery in expandedQueries)
        {
            var queryVector = embedder.GetEmbeddings(expandedQuery);
            var searchResults = await _vectorStore.SearchSimilarAsync(_collectionName, queryVector, initialTopK);
            
            var tenantResults = searchResults
                .Where(r => r.Payload.ContainsKey("TenantId") && r.Payload["TenantId"] == tenantId.ToString())
                .ToList();

            foreach (var r in tenantResults)
            {
                if (r.Payload.ContainsKey("Content"))
                {
                    allDenseResults.Add((r.Payload["Content"], r.Score, "dense"));
                }
            }
        }

        // === Stage 3: Sparse Retrieval (Keyword Matching — BM25-like) ===
        var sparseResults = await PerformKeywordSearchAsync(tenantId, query);

        // === Stage 4: Reciprocal Rank Fusion (RRF) ===
        var fusedResults = ReciprocalRankFusion(allDenseResults, sparseResults, topK * 3);

        if (!fusedResults.Any())
        {
            _logger.LogInformation("🔍 No relevant results found");
            return "Không tìm thấy dữ liệu liên quan.";
        }

        var candidateTexts = fusedResults.Select(r => r.Content).Distinct().ToList();

        if (!candidateTexts.Any()) return "Tài liệu bị rỗng nội dung.";

        // === Stage 5: Cross-Encoder Reranking ===
        var rerankerModel = await _modelManager.GetRerankerModelAsync();
        var ranker = new LMKit.Embeddings.Reranker(rerankerModel);

        var rankedScores = ranker.GetScore(query, candidateTexts.ToArray());

        var topResults = rankedScores
            .Select((score, index) => new { Score = score, Text = candidateTexts[index] })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        var contextBuilder = new System.Text.StringBuilder();
        foreach (var res in topResults)
        {
            contextBuilder.AppendLine(res.Text);
            contextBuilder.AppendLine("---");
        }

        _logger.LogInformation("🔍 Hybrid search complete: {DenseCount} dense + {SparseCount} sparse → {FinalCount} reranked results",
            allDenseResults.Count, sparseResults.Count, topResults.Count);

        return contextBuilder.ToString();
    }

    /// <summary>
    /// H3 Fix: True keyword-based sparse retrieval using Qdrant payload filtering.
    /// Previously this was faked by doing a vector search (top 50) then post-filtering by keywords,
    /// which meant documents outside the vector top-50 were invisible to keyword search.
    /// Now uses Qdrant's native payload filter — completely independent of vector similarity.
    /// </summary>
    private async Task<List<(string Content, float Score, string Source)>> PerformKeywordSearchAsync(Guid tenantId, string query)
    {
        var results = new List<(string Content, float Score, string Source)>();
        
        try
        {
            var keywords = _queryExpansion.ExtractKeywords(query);
            if (!keywords.Any()) return results;

            // H3 Fix: Use Qdrant's native payload filter for true sparse retrieval
            var payloadResults = await _vectorStore.SearchByPayloadFilterAsync(
                _collectionName,
                payloadField: "Keywords",
                keywords: keywords.ToList(),
                tenantFilterField: "TenantId",
                tenantId: tenantId.ToString(),
                topK: 20
            );

            foreach (var r in payloadResults)
            {
                if (!r.Payload.ContainsKey("Content")) continue;
                results.Add((r.Payload["Content"], r.Score, "sparse"));
            }

            _logger.LogInformation("H3 Sparse search: {Count} results from payload filter for {Keywords} keywords",
                results.Count, keywords.Count());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Keyword search failed, falling back to vector+filter: {Error}", ex.Message);
            
            // Fallback: original vector-based keyword filtering (graceful degradation)
            results = await PerformKeywordSearchFallbackAsync(tenantId, query);
        }

        return results;
    }

    /// <summary>
    /// Fallback sparse search — original vector-then-filter approach.
    /// Used when Qdrant payload index is not available.
    /// </summary>
    private async Task<List<(string Content, float Score, string Source)>> PerformKeywordSearchFallbackAsync(Guid tenantId, string query)
    {
        var results = new List<(string Content, float Score, string Source)>();
        var keywords = _queryExpansion.ExtractKeywords(query);
        if (!keywords.Any()) return results;

        var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
        var queryVector = embedder.GetEmbeddings(query);
        
        var broadResults = await _vectorStore.SearchSimilarAsync(_collectionName, queryVector, 50);
        var tenantResults = broadResults
            .Where(r => r.Payload.ContainsKey("TenantId") && r.Payload["TenantId"] == tenantId.ToString())
            .ToList();

        foreach (var r in tenantResults)
        {
            if (!r.Payload.ContainsKey("Content")) continue;
            
            var content = r.Payload["Content"].ToLowerInvariant();
            var keywordField = r.Payload.ContainsKey("Keywords") ? r.Payload["Keywords"].ToLowerInvariant() : "";
            var searchableText = content + " " + keywordField;
            
            var matchCount = keywords.Count(k => searchableText.Contains(k.ToLowerInvariant()));
            if (matchCount > 0)
            {
                var bm25Score = (float)matchCount / keywords.Count();
                results.Add((r.Payload["Content"], bm25Score, "sparse"));
            }
        }

        return results;
    }

    /// <summary>
    /// Reciprocal Rank Fusion (RRF) — merges dense and sparse results.
    /// RRF score = Σ 1/(k + rank_i) for each retrieval system.
    /// </summary>
    private List<(string Content, double FusedScore)> ReciprocalRankFusion(
        List<(string Content, float Score, string Source)> denseResults,
        List<(string Content, float Score, string Source)> sparseResults,
        int topK)
    {
        const int k = 60; // RRF constant
        var scoreMap = new Dictionary<string, double>();

        // Score from dense retrieval
        var denseRanked = denseResults
            .GroupBy(r => r.Content)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(r => r.Score)
            .Select((r, rank) => new { r.Content, Rank = rank + 1 })
            .ToList();

        foreach (var item in denseRanked)
        {
            if (!scoreMap.ContainsKey(item.Content)) scoreMap[item.Content] = 0;
            scoreMap[item.Content] += 1.0 / (k + item.Rank);
        }

        // Score from sparse retrieval
        var sparseRanked = sparseResults
            .GroupBy(r => r.Content)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(r => r.Score)
            .Select((r, rank) => new { r.Content, Rank = rank + 1 })
            .ToList();

        foreach (var item in sparseRanked)
        {
            if (!scoreMap.ContainsKey(item.Content)) scoreMap[item.Content] = 0;
            scoreMap[item.Content] += 1.0 / (k + item.Rank);
        }

        return scoreMap
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
