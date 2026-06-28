using System.Diagnostics;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Research Agent — specialized in web search + RAG knowledge retrieval.
/// Delegates: SearchWeb, QueryKnowledgeBase.
/// </summary>
public class ResearchAgent : ISpecializedAgent
{
    private readonly IRagPipelineService _ragService;
    private readonly IWebSearchService _webSearch;
    private readonly ILogger<ResearchAgent> _logger;

    public string AgentName => "ResearchAgent";
    public string Description => "Chuyên tìm kiếm thông tin từ web và kho tri thức nội bộ (RAG).";
    public IReadOnlyList<string> SupportedCategories => new[] { "rag", "search", "research", "knowledge" };

    public ResearchAgent(IRagPipelineService ragService, IWebSearchService webSearch, ILogger<ResearchAgent> logger)
    {
        _ragService = ragService;
        _webSearch = webSearch;
        _logger = logger;
    }

    public Task<double> EvaluateConfidenceAsync(string query, CancellationToken ct = default)
    {
        var lower = query.ToLowerInvariant();
        var researchKeywords = new[] { "tìm", "search", "tra cứu", "kiến thức", "knowledge", "tài liệu", "document",
            "thông tin", "research", "nguồn", "reference", "dữ liệu", "data", "hỏi", "ask" };
        var matchCount = researchKeywords.Count(k => lower.Contains(k));
        var confidence = Math.Min(matchCount * 0.25, 0.9);
        return Task.FromResult(confidence);
    }

    public async Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var tools = new List<string>();
        var results = new System.Text.StringBuilder();

        try
        {
            // Step 1: RAG Knowledge Base
            _logger.LogInformation("🔬 [{Agent}] Searching knowledge base...", AgentName);
            var ragResult = await _ragService.QueryKnowledgeBaseAsync(tenantId, query, topK: 3);
            if (!string.IsNullOrWhiteSpace(ragResult) && !ragResult.Contains("Không tìm thấy"))
            {
                results.AppendLine("[RAG Knowledge]: " + ragResult);
                tools.Add("QueryKnowledgeBase");
            }

            // Step 2: Web Search
            _logger.LogInformation("🔬 [{Agent}] Searching the web...", AgentName);
            var webResult = await _webSearch.SearchWebAsync(query, count: 3);
            if (!string.IsNullOrWhiteSpace(webResult))
            {
                results.AppendLine("[Web Search]: " + webResult);
                tools.Add("SearchWeb");
            }

            sw.Stop();
            return new AgentExecutionResult
            {
                AgentName = AgentName,
                Success = true,
                ResultContent = results.ToString(),
                ToolsUsed = tools,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "🔬 [{Agent}] Error during research", AgentName);
            return AgentExecutionResult.Fail(AgentName, ex.Message);
        }
    }
}
