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
    private readonly bool _enableHyDE;

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
        _enableHyDE = configuration.GetValue<bool>("RagSettings:EnableHyDE", false);
    }

    public async Task<string> IngestDocumentAsync(
        Guid tenantId,
        Guid userId,
        string fileName,
        string content,
        CancellationToken ct = default)
    {
        var embeddingModel = await _modelManager.GetEmbeddingModelAsync(ct: ct);
        await _vectorStore.EnsureCollectionExistsAsync(_collectionName, (ulong)embeddingModel.EmbeddingSize, ct);

        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
        var chunks = _chunkingService.ChunkText(content);
        int totalChunks = 0;

        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            float[] vector;
            await using (var inferenceLease = await _modelManager.AcquireEmbeddingInferenceAsync(ct))
                vector = embedder.GetEmbeddings(chunk);
            
            // Extract keywords for sparse search support
            var keywords = _queryExpansion.ExtractKeywords(chunk);
            
            var payload = new Dictionary<string, object>
            {
                { "TenantId", tenantId.ToString() },
                { "OwnerUserId", userId.ToString() },
                { "AccessScope", BuildPrivateAccessScope(tenantId, userId) },
                { "FileName", fileName },
                { "Content", chunk },
                { "ChunkIndex", chunkIndex },
                { "Keywords", string.Join(" ", keywords) } // Sparse search field
            };
            
            await _vectorStore.UpsertVectorAsync(_collectionName, Guid.NewGuid(), vector, payload, ct);
            totalChunks++;
        }

        _logger.LogInformation("📄 Ingested {Chunks} chunks from '{File}' for tenant {Tenant}", 
            totalChunks, fileName, tenantId);
        return $"Ingested {totalChunks} chunks from {fileName}.";
    }

    public async Task<string> QueryKnowledgeBaseAsync(
        Guid tenantId,
        Guid userId,
        string query,
        int topK = 3,
        CancellationToken ct = default,
        bool chatInferenceLeaseAlreadyHeld = false,
        IReadOnlyCollection<Guid>? documentIds = null)
    {
        _logger.LogInformation("Hybrid search starting for tenant {TenantId}; query length {QueryLength}",
            tenantId, query.Length);

        // Custom-agent knowledge pinning: chunks written by the document
        // vectorization worker carry a "DocumentId" payload key; when an
        // allowlist is provided, retrieval keeps only chunks of those documents
        // IN ADDITION to the access-scope filter (chunks without a DocumentId —
        // e.g. ad-hoc chat-attachment ingestion — are excluded by definition).
        var documentIdAllowlist = documentIds is { Count: > 0 }
            ? new HashSet<string>(
                documentIds.Where(id => id != Guid.Empty).Select(id => id.ToString()),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var embeddingModel = await _modelManager.GetEmbeddingModelAsync(ct: ct);
        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);

        // === Stage 1: Query Expansion ===
        var expandedQueries = await _queryExpansion.ExpandQueryAsync(
            query,
            maxExpansions: 2,
            ct: ct,
            chatInferenceLeaseAlreadyHeld: chatInferenceLeaseAlreadyHeld);
        _logger.LogInformation("Expanded to {Count} query variations", expandedQueries.Count);

        // === Stage 1.5: HyDE (Hypothetical Document Embeddings) ===
        if (_enableHyDE && IsComplexQuery(query))
        {
            try
            {
                var hydeDoc = await _queryExpansion.GenerateHypotheticalDocumentAsync(
                    query,
                    ct,
                    chatInferenceLeaseAlreadyHeld);
                expandedQueries.Add(hydeDoc);
                _logger.LogInformation("HyDE document generated and added to search queries");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HyDE generation failed, continuing without it: {Error}", ex.Message);
            }
        }

        // === Stage 2: Dense Retrieval (Vector Search) across all expanded queries ===
        var allDenseResults = new List<(string Content, float Score, string Source)>();
        int initialTopK = topK * 5;

        foreach (var expandedQuery in expandedQueries)
        {
            float[] queryVector;
            await using (var inferenceLease = await _modelManager.AcquireEmbeddingInferenceAsync(ct))
                queryVector = embedder.GetEmbeddings(expandedQuery);
            var tenantResults = documentIdAllowlist is null
                ? await _vectorStore.SearchSimilarWithAnyPayloadAsync(
                    _collectionName,
                    queryVector,
                    "AccessScope",
                    new[] { BuildPrivateAccessScope(tenantId, userId) },
                    initialTopK,
                    ct)
                : await _vectorStore.SearchSimilarWithinDocumentsAsync(
                    _collectionName,
                    queryVector,
                    "TenantId",
                    BuildTenantScope(tenantId),
                    "DocumentId",
                    documentIdAllowlist.ToList(),
                    initialTopK,
                    ct);

            foreach (var r in tenantResults)
            {
                if (r.Payload.ContainsKey("Content"))
                {
                    var source = BuildSourceLabel(r.Payload);
                    allDenseResults.Add((r.Payload["Content"], r.Score, source));
                }
            }
        }

        // === Stage 3: Sparse Retrieval (Keyword Matching — BM25-like) ===
        var sparseResults = await PerformKeywordSearchAsync(tenantId, userId, query, documentIdAllowlist, ct);

        // === Stage 4: Reciprocal Rank Fusion (RRF) ===
        var fusedResults = ReciprocalRankFusion(allDenseResults, sparseResults, topK * 3);

        if (!fusedResults.Any())
        {
            _logger.LogInformation("🔍 No relevant results found");
            return "Không tìm thấy dữ liệu liên quan.";
        }

        var candidateTexts = fusedResults.Select(r => r.Content).Distinct().ToList();
        var sourceByText = fusedResults
            .GroupBy(result => result.Content)
            .ToDictionary(group => group.Key, group => group.First().Source);

        if (!candidateTexts.Any()) return "Tài liệu bị rỗng nội dung.";

        // === Stage 5: Cross-Encoder Reranking ===
        var rerankerModel = await _modelManager.GetRerankerModelAsync(ct: ct);
        var ranker = new LMKit.Embeddings.Reranker(rerankerModel);

        float[] rankedScores;
        await using (var inferenceLease = await _modelManager.AcquireRerankerInferenceAsync(ct))
            rankedScores = ranker.GetScore(query, candidateTexts.ToArray());

        var topResults = rankedScores
            .Select((score, index) => new
            {
                Score = score,
                Text = candidateTexts[index],
                Source = sourceByText[candidateTexts[index]]
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        var contextBuilder = new System.Text.StringBuilder();
        foreach (var res in topResults)
        {
            contextBuilder.AppendLine($"[Source: {res.Source}]");
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
    /// When a <paramref name="documentIdAllowlist"/> is active (custom-agent knowledge
    /// pinning, possibly a SHARED agent), retrieval is tenant-scoped and constrained
    /// to the allowlist in-query via SearchByPayloadWithinDocumentsAsync; the ordinary
    /// (non-pinned) path stays private-scoped to the caller's AccessScope. The
    /// post-filter below on "DocumentId" is kept as a defensive backstop — it can
    /// only remove results, never add any, so access scoping is unaffected.
    /// </summary>
    private async Task<List<(string Content, float Score, string Source)>> PerformKeywordSearchAsync(
        Guid tenantId,
        Guid userId,
        string query,
        IReadOnlySet<string>? documentIdAllowlist,
        CancellationToken ct)
    {
        var results = new List<(string Content, float Score, string Source)>();

        try
        {
            var keywords = _queryExpansion.ExtractKeywords(query);
            if (!keywords.Any()) return results;

            // H3 Fix: Use Qdrant's native payload filter for true sparse retrieval.
            // Non-pinned chat retrieval stays private-scoped (caller's AccessScope);
            // the pinned-knowledge path (shared custom agent) is tenant-scoped +
            // document-allowlisted so docs owned by another tenant member resolve.
            var payloadResults = documentIdAllowlist is null
                ? await _vectorStore.SearchByPayloadFilterAsync(
                    _collectionName,
                    payloadField: "Keywords",
                    keywords: keywords.ToList(),
                    tenantFilterField: "AccessScope",
                    tenantId: BuildPrivateAccessScope(tenantId, userId),
                    topK: 20,
                    ct: ct)
                : await _vectorStore.SearchByPayloadWithinDocumentsAsync(
                    _collectionName,
                    payloadField: "Keywords",
                    keywords: keywords.ToList(),
                    tenantField: "TenantId",
                    tenantId: BuildTenantScope(tenantId),
                    documentIdField: "DocumentId",
                    documentIds: documentIdAllowlist.ToList(),
                    topK: 20,
                    ct: ct);

            foreach (var r in payloadResults)
            {
                if (!r.Payload.ContainsKey("Content")) continue;
                if (!MatchesDocumentAllowlist(r.Payload, documentIdAllowlist)) continue;
                var source = BuildSourceLabel(r.Payload);
                results.Add((r.Payload["Content"], r.Score, source));
            }

            _logger.LogInformation("H3 Sparse search: {Count} results from payload filter for {Keywords} keywords",
                results.Count, keywords.Count());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Keyword search failed, falling back to vector+filter: {Error}", ex.Message);

            // Fallback: original vector-based keyword filtering (graceful degradation)
            results = await PerformKeywordSearchFallbackAsync(tenantId, userId, query, documentIdAllowlist, ct);
        }

        return results;
    }

    /// <summary>
    /// True when no document allowlist is active, or when the chunk's "DocumentId"
    /// payload is in it. Chunks without a DocumentId (ad-hoc knowledge ingestion)
    /// never match an active allowlist.
    /// </summary>
    private static bool MatchesDocumentAllowlist(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlySet<string>? documentIdAllowlist)
    {
        if (documentIdAllowlist is null) return true;
        return payload.TryGetValue("DocumentId", out var documentId)
            && documentIdAllowlist.Contains(documentId);
    }

    /// <summary>
    /// Fallback sparse search — original vector-then-filter approach.
    /// Used when Qdrant payload index is not available. An active document
    /// allowlist rides on the doc-constrained dense search, so the fallback
    /// honors it natively too.
    /// </summary>
    private async Task<List<(string Content, float Score, string Source)>> PerformKeywordSearchFallbackAsync(
        Guid tenantId,
        Guid userId,
        string query,
        IReadOnlySet<string>? documentIdAllowlist,
        CancellationToken ct)
    {
        var results = new List<(string Content, float Score, string Source)>();
        var keywords = _queryExpansion.ExtractKeywords(query);
        if (!keywords.Any()) return results;

        var embeddingModel = await _modelManager.GetEmbeddingModelAsync(ct: ct);
        var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
        float[] queryVector;
        await using (var inferenceLease = await _modelManager.AcquireEmbeddingInferenceAsync(ct))
            queryVector = embedder.GetEmbeddings(query);

        var tenantResults = documentIdAllowlist is null
            ? await _vectorStore.SearchSimilarWithAnyPayloadAsync(
                _collectionName,
                queryVector,
                "AccessScope",
                new[] { BuildPrivateAccessScope(tenantId, userId) },
                50,
                ct)
            : await _vectorStore.SearchSimilarWithinDocumentsAsync(
                _collectionName,
                queryVector,
                "TenantId",
                BuildTenantScope(tenantId),
                "DocumentId",
                documentIdAllowlist.ToList(),
                50,
                ct);

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
                var source = BuildSourceLabel(r.Payload);
                results.Add((r.Payload["Content"], bm25Score, source));
            }
        }

        return results;
    }

    /// <summary>
    /// Reciprocal Rank Fusion (RRF) — merges dense and sparse results.
    /// RRF score = Σ 1/(k + rank_i) for each retrieval system.
    /// </summary>
    private List<(string Content, double FusedScore, string Source)> ReciprocalRankFusion(
        List<(string Content, float Score, string Source)> denseResults,
        List<(string Content, float Score, string Source)> sparseResults,
        int topK)
    {
        const int k = 60; // RRF constant
        var scoreMap = new Dictionary<string, double>();
        var sourceMap = denseResults.Concat(sparseResults)
            .GroupBy(result => result.Content)
            .ToDictionary(group => group.Key, group => group.First().Source);

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
            .Select(kv => (kv.Key, kv.Value, sourceMap[kv.Key]))
            .ToList();
    }

    /// <summary>
    /// Xác định xem query có đủ phức tạp để kích hoạt HyDE hay không.
    /// HyDE hiệu quả với câu hỏi dạng khái niệm, so sánh, giải thích — không cần cho từ khóa đơn giản.
    /// </summary>
    private static bool IsComplexQuery(string query)
    {
        var complexPrefixes = new[] { "giải thích", "tại sao", "so sánh", "phân tích", "đánh giá", 
                                      "explain", "why", "compare", "analyze", "how does" };
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        // Dài > 8 từ hoặc bắt đầu bằng từ khóa phức tạp
        return words.Length > 8 || complexPrefixes.Any(p => query.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPrivateAccessScope(Guid tenantId, Guid userId) =>
        $"private:{tenantId:N}:{userId:N}";

    // Tenant-wide scope value for the pinned-knowledge (shared-agent) path.
    // Must match the "TenantId" payload the vectorization worker writes, which is
    // the Guid default ("D") format WITH hyphens — NOT the ":N" format used
    // inside the private AccessScope composite.
    private static string BuildTenantScope(Guid tenantId) => tenantId.ToString();

    private static string BuildSourceLabel(IReadOnlyDictionary<string, string> payload)
    {
        var fileName = payload.TryGetValue("FileName", out var file) ? file : "knowledge-base";
        return payload.TryGetValue("ChunkIndex", out var chunkIndex)
            ? $"{fileName}#chunk-{chunkIndex}"
            : fileName;
    }
}
