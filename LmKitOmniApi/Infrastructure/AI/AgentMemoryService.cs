using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Persistent agent memory service using PostgreSQL (AgentMemory entity).
/// Supports fact extraction, semantic recall, and context injection.
/// Inspired by console_net/ai-agents/agent-memory.
/// </summary>
public class AgentMemoryService : IAgentMemoryService
{
    private readonly HermesDbContext _dbContext;
    private readonly LmModelManager _modelManager;
    private readonly IVectorStoreService _vectorStore;
    private readonly ILogger<AgentMemoryService> _logger;
    private readonly IDistributedCache _cache;

    public AgentMemoryService(HermesDbContext dbContext, LmModelManager modelManager, IVectorStoreService vectorStore, ILogger<AgentMemoryService> logger, IDistributedCache cache)
    {
        _dbContext = dbContext;
        _modelManager = modelManager;
        _vectorStore = vectorStore;
        _logger = logger;
        _cache = cache;
    }

    public async Task<Guid> StoreMemoryAsync(Guid tenantId, Guid? userId, string memoryType, string key, string value,
        string? sourceContext = null, float confidence = 0.5f, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        // Check if a memory with the same key exists for this user/tenant
        var existing = await _dbContext.AgentMemories
            .FirstOrDefaultAsync(m => m.TenantId == tenantId 
                && m.UserId == userId 
                && m.MemoryKey == key 
                && m.MemoryType == memoryType, ct);

        if (existing != null)
        {
            // Update existing memory with higher confidence
            existing.MemoryValue = value;
            existing.Confidence = Math.Max(existing.Confidence, confidence);
            existing.SourceContext = sourceContext ?? existing.SourceContext;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            existing.ExpiresAtUtc = expiresAt;
            
            await _dbContext.SaveChangesAsync(ct);

            var cacheKeyExisting = $"AgentMemories:{tenantId}:{userId}";
            await _cache.RemoveAsync(cacheKeyExisting, ct);

            _logger.LogInformation("🧠 Updated memory: [{Type}] {Key} = {Value}", memoryType, key, 
                value.Length > 50 ? value.Substring(0, 50) + "..." : value);
            return existing.Id;
        }

        var memory = new AgentMemory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            MemoryType = memoryType,
            MemoryKey = key,
            MemoryValue = value,
            SourceContext = sourceContext,
            Confidence = confidence,
            ExpiresAtUtc = expiresAt,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _dbContext.AgentMemories.Add(memory);
        await _dbContext.SaveChangesAsync(ct);

        var cacheKey = $"AgentMemories:{tenantId}:{userId}";
        await _cache.RemoveAsync(cacheKey, ct);

        _logger.LogInformation("🧠 Stored new memory: [{Type}] {Key} = {Value}", memoryType, key,
            value.Length > 50 ? value.Substring(0, 50) + "..." : value);
        return memory.Id;
    }

    /// <summary>
    /// H2 Fix: Hybrid memory recall — keyword matching + semantic embedding scoring.
    /// Previously only keyword matching was used, which fails for Vietnamese word boundaries
    /// and cannot find semantic relationships.
    /// </summary>
    public async Task<List<MemoryRecallResult>> RecallMemoriesAsync(Guid tenantId, Guid? userId, string query, int maxResults = 5, CancellationToken ct = default)
    {
        var cacheKey = $"AgentMemories:{tenantId}:{userId}";
        List<AgentMemory>? candidates = null;
        var cachedMemories = await _cache.GetStringAsync(cacheKey, ct);
        
        if (!string.IsNullOrEmpty(cachedMemories))
        {
            try
            {
                candidates = JsonSerializer.Deserialize<List<AgentMemory>>(cachedMemories, new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.IgnoreCycles
                });
            }
            catch { /* fallback to db */ }
        }

        if (candidates == null)
        {
            candidates = await _dbContext.AgentMemories
                .Where(m => m.TenantId == tenantId
                    && (m.UserId == null || m.UserId == userId)
                    && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > DateTime.UtcNow))
                .OrderByDescending(m => m.UpdatedAtUtc)
                .Take(200)
                .ToListAsync(ct);
                
            try
            {
                var json = JsonSerializer.Serialize(candidates, new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.IgnoreCycles
                });
                await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                }, ct);
            }
            catch { /* ignore cache set errors */ }
        }
        else
        {
            // Filter out any expired memories that might be in the cache
            candidates = candidates.Where(m => m.ExpiresAtUtc == null || m.ExpiresAtUtc > DateTime.UtcNow).ToList();
        }

        if (!candidates.Any()) return new List<MemoryRecallResult>();

        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

        // H2 Fix: Semantic scoring via embeddings
        Dictionary<string, float>? semanticScores = null;
        try
        {
            var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
            var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
            var queryEmbedding = embedder.GetEmbeddings(query);

            semanticScores = new Dictionary<string, float>();
            foreach (var m in candidates)
            {
                var memoryText = $"{m.MemoryKey} {m.MemoryValue}";
                var memoryEmbedding = embedder.GetEmbeddings(memoryText);
                semanticScores[m.Id.ToString()] = CosineSimilarity(queryEmbedding, memoryEmbedding);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Semantic memory scoring failed, using keyword-only: {Error}", ex.Message);
        }

        // H2 Fix: Weighted blend — semantic (50%) + keyword (30%) + recency (15%) + confidence (5%)
        var scored = candidates.Select(m =>
        {
            var memoryText = $"{m.MemoryKey} {m.MemoryValue}".ToLowerInvariant();
            var keywordScore = queryWords.Count > 0
                ? queryWords.Count(w => memoryText.Contains(w)) / (double)queryWords.Count
                : 0.0;
            
            var semanticScore = semanticScores != null && semanticScores.TryGetValue(m.Id.ToString(), out var ss)
                ? Math.Max(0, ss)
                : 0.0;

            var ageHours = (DateTime.UtcNow - m.UpdatedAtUtc).TotalHours;
            var recencyScore = Math.Max(0, 1.0 - (ageHours / 720.0));
            
            var combinedScore = (keywordScore * 0.30) + (semanticScore * 0.50) + (recencyScore * 0.15) + (m.Confidence * 0.05);

            return new MemoryRecallResult
            {
                Id = m.Id,
                MemoryType = m.MemoryType,
                Key = m.MemoryKey,
                Value = m.MemoryValue,
                Confidence = m.Confidence,
                RelevanceScore = combinedScore,
                CreatedAtUtc = m.CreatedAtUtc
            };
        })
        .Where(r => r.RelevanceScore > 0.1)
        .OrderByDescending(r => r.RelevanceScore)
        .Take(maxResults)
        .ToList();

        _logger.LogInformation("Recalled {Count} memories (hybrid) for: '{Query}'", scored.Count,
            query.Length > 50 ? query.Substring(0, 50) + "..." : query);

        return scored;
    }

    /// <summary>Cosine similarity between two embedding vectors.</summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }

    public async Task ExtractAndStoreFactsAsync(Guid tenantId, Guid? userId, string userMessage, string assistantResponse, CancellationToken ct = default)
    {
        // Simple heuristic fact extraction (LLM-based extraction would be Phase 2 enhancement)
        var facts = ExtractFactsHeuristic(userMessage);
        
        foreach (var (key, value, type) in facts)
        {
            await StoreMemoryAsync(tenantId, userId, type, key, value, 
                sourceContext: userMessage.Length > 200 ? userMessage.Substring(0, 200) : userMessage,
                confidence: 0.6f, ct: ct);
            
            // Phase 3: Graph Memory Vector Storage
            // Cập nhật Qdrant: Lưu fact dưới dạng string relationship để semantic search tốt hơn
            try
            {
                var collectionName = $"graph_memory_{tenantId:N}";
                await _vectorStore.EnsureCollectionExistsAsync(collectionName, 384);
                
                var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
                var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
                var factString = $"User {userId} ({type}) {key}: {value}";
                var vector = embedder.GetEmbeddings(factString);
                
                var payload = new Dictionary<string, object>
                {
                    { "UserId", userId?.ToString() ?? "Anonymous" },
                    { "Key", key },
                    { "Value", value },
                    { "Type", type },
                    { "Fact", factString }
                };

                await _vectorStore.UpsertVectorAsync(collectionName, Guid.NewGuid(), vector, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store graph memory fact in vector DB.");
            }
        }
    }

    public async Task<string> GetMemoryContextAsync(Guid tenantId, Guid? userId, string currentQuery, CancellationToken ct = default)
    {
        var memories = await RecallMemoriesAsync(tenantId, userId, currentQuery, maxResults: 5, ct);
        
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("\n--- Agent Memory (Recalled Facts) ---");
        
        foreach (var m in memories)
        {
            builder.AppendLine($"• [{m.MemoryType}] {m.Key}: {m.Value}");
        }
        
        // Phase 3: Semantic recall from Graph Memory
        try
        {
            var collectionName = $"graph_memory_{tenantId:N}";
            var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
            var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
            var queryVector = embedder.GetEmbeddings(currentQuery);
            var graphResults = await _vectorStore.SearchSimilarAsync(collectionName, queryVector, 3);
            
            foreach (var res in graphResults.Where(r => r.Score > 0.6f))
            {
                if (res.Payload != null && res.Payload.TryGetValue("Fact", out var factVal))
                {
                    builder.AppendLine($"• [Graph Fact] {factVal}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graph memory collection might not exist yet.");
        }
        
        builder.AppendLine("--- End Memory ---");
        return builder.ToString();
    }

    public async Task CleanupExpiredMemoriesAsync(CancellationToken ct = default)
    {
        var expired = await _dbContext.AgentMemories
            .Where(m => m.ExpiresAtUtc != null && m.ExpiresAtUtc < DateTime.UtcNow)
            .ToListAsync(ct);

        if (expired.Any())
        {
            _dbContext.AgentMemories.RemoveRange(expired);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("🧠 Cleaned up {Count} expired memories", expired.Count);
        }
    }

    /// <summary>
    /// Simple heuristic fact extraction from user messages.
    /// Looks for patterns like "my name is X", "I prefer Y", "I live in Z".
    /// </summary>
    private List<(string Key, string Value, string Type)> ExtractFactsHeuristic(string message)
    {
        var facts = new List<(string Key, string Value, string Type)>();
        var lower = message.ToLowerInvariant();

        // Name patterns
        var namePatterns = new[]
        {
            @"(?i)(?:my\s+name\s+is|i'?m|call\s+me|tên\s+(?:tôi|mình|em)\s+là)\s+([A-ZÀ-Ỹ][a-zà-ỹ]+(?:\s+[A-ZÀ-Ỹ][a-zà-ỹ]+)*)",
        };

        foreach (var pattern in namePatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(message, pattern);
            if (match.Success && match.Groups.Count > 1)
            {
                facts.Add(("user_name", match.Groups[1].Value.Trim(), "UserProfile"));
            }
        }

        // Preference patterns
        if (lower.Contains("prefer") || lower.Contains("thích") || lower.Contains("like"))
        {
            var prefMatch = System.Text.RegularExpressions.Regex.Match(message, 
                @"(?i)(?:i\s+prefer|tôi\s+thích|i\s+like)\s+(.+?)(?:\.|$)", 
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (prefMatch.Success)
            {
                var pref = prefMatch.Groups[1].Value.Trim();
                if (pref.Length > 3 && pref.Length < 200)
                {
                    facts.Add(("user_preference", pref, "Preference"));
                }
            }
        }

        // Language preference
        if (lower.Contains("speak") || lower.Contains("language") || lower.Contains("tiếng"))
        {
            var langMatch = System.Text.RegularExpressions.Regex.Match(message,
                @"(?i)(?:speak|use|write\s+in|dùng|viết\s+bằng)\s+(english|vietnamese|tiếng\s+việt|tiếng\s+anh|french|chinese|japanese)",
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (langMatch.Success)
            {
                facts.Add(("preferred_language", langMatch.Groups[1].Value.Trim(), "Preference"));
            }
        }

        return facts;
    }
}
