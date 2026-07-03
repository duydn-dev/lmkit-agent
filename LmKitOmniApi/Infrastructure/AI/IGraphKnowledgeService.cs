using Neo4j.Driver;
using Microsoft.Extensions.Logging;
using LmKitOmniApi.Services;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI;

public interface IGraphKnowledgeService
{
    Task ExtractAndStoreEntitiesAsync(string text, string tenantId, CancellationToken ct = default);
    Task<string> QueryGraphAsync(string query, string tenantId, CancellationToken ct = default);
}

public class GraphKnowledgeService : IGraphKnowledgeService
{
    private readonly IDriver _driver;
    private readonly LmModelManager _modelManager;
    private readonly ILogger<GraphKnowledgeService> _logger;

    public GraphKnowledgeService(IDriver driver, LmModelManager modelManager, ILogger<GraphKnowledgeService> logger)
    {
        _driver = driver;
        _modelManager = modelManager;
        _logger = logger;
    }

    public async Task ExtractAndStoreEntitiesAsync(string text, string tenantId, CancellationToken ct = default)
    {
        // 1. Dùng LLM để trích xuất Entity và Relationship
        var model = await _modelManager.GetChatModelAsync();
        var chat = new MultiTurnConversation(model);
        chat.SystemPrompt = "Bạn là chuyên gia trích xuất thông tin. Hãy đọc đoạn văn bản và trích xuất các thực thể và mối quan hệ theo định dạng đúng 3 phần ngăn cách bằng dấu phẩy: [Entity1], [Relationship], [Entity2]. Ví dụ: John, WORKS_AT, Google. KHÔNG in thêm văn bản nào khác, mỗi mối quan hệ trên một dòng.";
        
        var result = chat.Submit(text);
        var lines = result.Completion.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        await using var session = _driver.AsyncSession();

        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length == 3)
            {
                var e1 = parts[0].Trim().Replace("'", "");
                var rel = parts[1].Trim().Replace(" ", "_").ToUpper();
                var e2 = parts[2].Trim().Replace("'", "");

                if (string.IsNullOrWhiteSpace(e1) || string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(e2)) continue;

                // 2. Cypher lưu vào Neo4j (Merge để không trùng lặp)
                var cypher = $@"
                    MERGE (a:Entity {{name: $e1, tenantId: $tenantId}})
                    MERGE (b:Entity {{name: $e2, tenantId: $tenantId}})
                    MERGE (a)-[r:`{rel}`]->(b)
                ";

                await session.RunAsync(cypher, new { e1, e2, tenantId });
            }
        }
        _logger.LogInformation("✅ Extracted and stored entities into Neo4j.");
    }

    public async Task<string> QueryGraphAsync(string query, string tenantId, CancellationToken ct = default)
    {
        // 1. Trích xuất Entity chính từ Query bằng LLM
        var model = await _modelManager.GetChatModelAsync();
        var chat = new MultiTurnConversation(model);
        chat.SystemPrompt = "Trích xuất danh từ/thực thể chính yếu nhất từ câu hỏi này. CHỈ trả về đúng 1 từ/cụm từ.";
        
        var targetEntity = chat.Submit(query).Completion.Trim();
        if (string.IsNullOrEmpty(targetEntity)) return string.Empty;

        // 2. Tìm kiếm các Node lân cận (Multi-hop)
        await using var session = _driver.AsyncSession();
        var cypher = @"
            MATCH (a:Entity {name: $targetEntity, tenantId: $tenantId})-[r]-(b)
            RETURN a.name AS Source, type(r) AS Rel, b.name AS Target
            LIMIT 10
        ";

        var result = await session.RunAsync(cypher, new { targetEntity, tenantId });
        var records = await result.ToListAsync(ct);

        if (records.Count == 0) return string.Empty;

        var context = "Theo cơ sở dữ liệu đồ thị:\n";
        foreach (var record in records)
        {
            context += $"- {record["Source"]} {record["Rel"]} {record["Target"]}\n";
        }
        return context;
    }
}
