using System.Diagnostics;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Research Agent — specialized in web search + RAG knowledge retrieval.
/// Delegates: SearchWeb, QueryKnowledgeBase.
/// </summary>
public class ResearchAgent : ISpecializedAgent
{
    private readonly IRagPipelineService _ragService;
    private readonly IWebSearchService _webSearch;
    private readonly AgentToolGateway _toolGateway;
    private readonly ILogger<ResearchAgent> _logger;

    public string AgentName => "ResearchAgent";
    public string Description => "Chuyên tìm kiếm thông tin từ web và kho tri thức nội bộ (RAG).";
    public IReadOnlyList<string> SupportedCategories => new[] { "rag", "search", "research", "knowledge" };

    public ResearchAgent(
        IRagPipelineService ragService,
        IWebSearchService webSearch,
        AgentToolGateway toolGateway,
        ILogger<ResearchAgent> logger)
    {
        _ragService = ragService;
        _webSearch = webSearch;
        _toolGateway = toolGateway;
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

    public async Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string userRole, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var tools = new List<string>();
        var results = new System.Text.StringBuilder();

        try
        {
            // Step 1: RAG Knowledge Base
            _logger.LogInformation("🔬 [{Agent}] Searching knowledge base...", AgentName);
            var ragExecution = await _toolGateway.ExecuteReadOnlyAsync(
                tenantId, userId, userRole, "QueryKnowledgeBase", query,
                token => _ragService.QueryKnowledgeBaseAsync(
                    tenantId,
                    userId ?? Guid.Empty,
                    query,
                    topK: 3,
                    ct: token,
                    chatInferenceLeaseAlreadyHeld: true), ct);
            if (ragExecution.IsSuccess
                && !string.IsNullOrWhiteSpace(ragExecution.Output)
                && !ragExecution.Output.Contains("Không tìm thấy"))
            {
                results.AppendLine("[RAG Knowledge]: " + ragExecution.Output);
                tools.Add("QueryKnowledgeBase");
            }

            // Step 2: Web Search
            _logger.LogInformation("🔬 [{Agent}] Searching the web...", AgentName);
            var webExecution = await _toolGateway.ExecuteReadOnlyAsync(
                tenantId, userId, userRole, "SearchWeb", query,
                token => _webSearch.SearchWebAsync(query, count: 3, token), ct);
            if (webExecution.IsSuccess && !string.IsNullOrWhiteSpace(webExecution.Output))
            {
                results.AppendLine("[Web Search]: " + webExecution.Output);
                tools.Add("SearchWeb");
            }

            if (!ragExecution.IsSuccess && !webExecution.IsSuccess)
            {
                return AgentExecutionResult.Fail(
                    AgentName,
                    ragExecution.ErrorMessage ?? webExecution.ErrorMessage ?? "Research tools were unavailable.");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "🔬 [{Agent}] Error during research", AgentName);
            return AgentExecutionResult.Fail(AgentName, ex.Message);
        }
    }
}
