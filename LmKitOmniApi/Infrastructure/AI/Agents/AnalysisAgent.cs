using System.Diagnostics;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using MediatR;
using Microsoft.Extensions.Logging;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Analysis Agent — specialized in NLP tasks: sentiment analysis, NER, PII detection.
/// Delegates: AnalyzeText.
/// </summary>
public class AnalysisAgent : ISpecializedAgent
{
    private readonly IMediator _mediator;
    private readonly AgentToolGateway _toolGateway;
    private readonly ILogger<AnalysisAgent> _logger;

    public string AgentName => "AnalysisAgent";
    public string Description => "Chuyên phân tích văn bản: sentiment, trích xuất thực thể (NER), phát hiện PII.";
    public IReadOnlyList<string> SupportedCategories => new[] { "analysis", "nlp", "sentiment", "ner", "pii", "reasoning" };

    public AnalysisAgent(IMediator mediator, AgentToolGateway toolGateway, ILogger<AnalysisAgent> logger)
    {
        _mediator = mediator;
        _toolGateway = toolGateway;
        _logger = logger;
    }

    public Task<double> EvaluateConfidenceAsync(string query, CancellationToken ct = default)
    {
        var lower = query.ToLowerInvariant();
        var analysisKeywords = new[] { "phân tích", "analyze", "sentiment", "cảm xúc", "thực thể", "entity",
            "pii", "nhận dạng", "classify", "phân loại", "đánh giá", "evaluate", "review" };
        var matchCount = analysisKeywords.Count(k => lower.Contains(k));
        var confidence = Math.Min(matchCount * 0.3, 0.9);
        return Task.FromResult(confidence);
    }

    public async Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string userRole, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("📊 [{Agent}] Analyzing text...", AgentName);
            var execution = await _toolGateway.ExecuteReadOnlyAsync(
                tenantId, userId, userRole, "AnalyzeText", null,
                async token =>
                {
                    var analysis = await _mediator.Send(new AnalyzeTextCommand { Text = query }, token);
                    return $"Sentiment: {analysis.Sentiment}, Entities: {string.Join(", ", analysis.ExtractedEntities)}";
                }, ct);

            if (!execution.IsSuccess)
                return AgentExecutionResult.Fail(AgentName, execution.ErrorMessage ?? "Text analysis failed.");

            var content = execution.Output;

            sw.Stop();
            return new AgentExecutionResult
            {
                AgentName = AgentName,
                Success = true,
                ResultContent = content,
                ToolsUsed = new List<string> { "AnalyzeText" },
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "📊 [{Agent}] Error during analysis", AgentName);
            return AgentExecutionResult.Fail(AgentName, ex.Message);
        }
    }
}
