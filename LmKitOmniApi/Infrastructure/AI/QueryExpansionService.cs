using LMKit.TextGeneration;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Query expansion service for improving RAG retrieval.
/// Generates synonyms, multi-query variations, and HyDE (Hypothetical Document Embeddings).
/// Inspired by console_net/rag-and-knowledge/query-expansion.
/// </summary>
public class QueryExpansionService
{
    private readonly LmModelManager _modelManager;
    private readonly ILogger<QueryExpansionService> _logger;

    public QueryExpansionService(LmModelManager modelManager, ILogger<QueryExpansionService> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    /// <summary>
    /// Expand a query by generating synonym variations + multi-perspective rewrites.
    /// Returns the original query plus expanded versions for broader retrieval.
    /// </summary>
    public async Task<List<string>> ExpandQueryAsync(string query, int maxExpansions = 3, CancellationToken ct = default)
    {
        var expansions = new List<string> { query }; // Always include original

        try
        {
            var model = await _modelManager.GetChatModelAsync();
            var chat = new MultiTurnConversation(model);
            chat.SystemPrompt = @"Bạn là một chuyên gia tìm kiếm thông tin. 
Nhiệm vụ: Viết lại câu hỏi của người dùng thành các biến thể khác nhau để tìm kiếm toàn diện hơn.
Quy tắc:
- Mỗi biến thể trên 1 dòng
- Sử dụng từ đồng nghĩa và cách diễn đạt khác
- Không thêm thông tin mới, chỉ viết lại
- Viết tối đa " + maxExpansions + @" biến thể
- Output CHỈ các biến thể, không giải thích

Ví dụ:
Input: 'Cách tối ưu hiệu suất database'
Output:
Phương pháp cải thiện performance cơ sở dữ liệu
Kỹ thuật tăng tốc truy vấn database
Tối ưu hóa query và index database";

            var result = chat.Submit($"Viết lại câu hỏi: '{query}'");
            
            var lines = result.Completion
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length > 5)
                .Take(maxExpansions)
                .ToList();

            expansions.AddRange(lines);
            
            _logger.LogInformation("🔍 Query expanded: '{Original}' → {Count} variations", 
                query.Length > 50 ? query.Substring(0, 50) + "..." : query, expansions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Query expansion failed, using original query only. Error: {Error}", ex.Message);
        }

        return expansions;
    }

    /// <summary>
    /// Generate a hypothetical document that would answer the query (HyDE technique).
    /// The embedding of this hypothetical doc often retrieves better results than the raw query.
    /// </summary>
    public async Task<string> GenerateHypotheticalDocumentAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var model = await _modelManager.GetChatModelAsync();
            var chat = new MultiTurnConversation(model);
            chat.SystemPrompt = @"Bạn là chuyên gia viết tài liệu. Hãy viết một đoạn văn ngắn (100-200 từ) 
mô tả câu trả lời chi tiết cho câu hỏi bên dưới, như thể đây là đoạn trích từ tài liệu chính thức.
Chỉ viết nội dung, không thêm tiêu đề hay giải thích.";

            var result = chat.Submit(query);
            
            _logger.LogInformation("📄 HyDE generated for: '{Query}'", 
                query.Length > 50 ? query.Substring(0, 50) + "..." : query);
            
            return result.Completion.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ HyDE generation failed: {Error}", ex.Message);
            return query; // Fallback to original query
        }
    }

    /// <summary>
    /// Extract keywords from a query for keyword-based search (BM25 complement).
    /// </summary>
    public List<string> ExtractKeywords(string query)
    {
        // Vietnamese + English stopwords
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Vietnamese
            "và", "của", "là", "cho", "trong", "với", "có", "được", "đã", "sẽ", "đang",
            "này", "đó", "các", "những", "một", "không", "từ", "về", "ra", "vào",
            "tôi", "bạn", "chúng", "họ", "nó", "ai", "gì", "nào", "sao", "thế",
            "rất", "cũng", "nhưng", "nếu", "khi", "thì", "mà", "vì", "do", "bởi",
            "hơn", "nhất", "lại", "đã", "rồi", "hay", "hoặc", "cả", "mỗi", "nên",
            // English  
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "can", "shall", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "as", "into", "through", "during",
            "before", "after", "above", "below", "between", "out", "off", "over",
            "under", "again", "further", "then", "once", "here", "there", "when",
            "where", "why", "how", "all", "each", "every", "both", "few", "more",
            "most", "other", "some", "such", "no", "nor", "not", "only", "own",
            "same", "so", "than", "too", "very", "just", "because", "but", "and",
            "if", "or", "while", "about", "what", "which", "who", "this", "that"
        };

        var words = System.Text.RegularExpressions.Regex.Split(query, @"[\s\p{P}]+")
            .Where(w => w.Length > 1 && !stopwords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return words;
    }
}
