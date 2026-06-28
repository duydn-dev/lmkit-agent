using System.Diagnostics;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Vision Agent — specialized in OCR, image understanding, chat-with-image.
/// Auto-saves OCR results to Qdrant for future knowledge recall.
/// Delegates: AnalyzeImage, OCR → Qdrant ingestion.
/// </summary>
public class VisionAgent : ISpecializedAgent
{
    private readonly IMediator _mediator;
    private readonly IRagPipelineService _ragService;
    private readonly ILogger<VisionAgent> _logger;

    public string AgentName => "VisionAgent";
    public string Description => "Chuyên xử lý hình ảnh: OCR, nhận dạng nội dung, trích xuất text từ ảnh/PDF. Tự động lưu kết quả vào kho tri thức.";
    public IReadOnlyList<string> SupportedCategories => new[] { "vision", "ocr", "image", "photo", "picture" };

    public VisionAgent(IMediator mediator, IRagPipelineService ragService, ILogger<VisionAgent> logger)
    {
        _mediator = mediator;
        _ragService = ragService;
        _logger = logger;
    }

    public Task<double> EvaluateConfidenceAsync(string query, CancellationToken ct = default)
    {
        var lower = query.ToLowerInvariant();
        // High confidence if query mentions image file extensions
        if (lower.Contains(".jpg") || lower.Contains(".png") || lower.Contains(".jpeg") || lower.Contains(".bmp") || lower.Contains(".webp"))
            return Task.FromResult(0.95);

        var visionKeywords = new[] { "ảnh", "image", "hình", "photo", "ocr", "nhận dạng", "picture", "scan",
            "chữ trong ảnh", "đọc ảnh", "xem ảnh", "mô tả ảnh", "describe image" };
        var matchCount = visionKeywords.Count(k => lower.Contains(k));
        var confidence = Math.Min(matchCount * 0.3, 0.9);
        return Task.FromResult(confidence);
    }

    public async Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var tools = new List<string>();

        try
        {
            // Extract image path from query
            var parts = query.Split(' ');
            var imagePath = parts.FirstOrDefault(p =>
                p.Contains(".jpg") || p.Contains(".png") || p.Contains(".jpeg") ||
                p.Contains(".bmp") || p.Contains(".webp"));

            if (string.IsNullOrEmpty(imagePath))
            {
                sw.Stop();
                return AgentExecutionResult.Fail(AgentName, "Không tìm thấy đường dẫn hình ảnh trong yêu cầu.");
            }

            // Step 1: Analyze image (OCR / VLM)
            _logger.LogInformation("👁️ [{Agent}] Analyzing image: {Path}", AgentName, imagePath);
            var visionResult = await _mediator.Send(new AnalyzeImageCommand { ImagePath = imagePath }, ct);
            tools.Add("AnalyzeImage");

            // Step 2: Auto-save OCR result to Qdrant for future knowledge recall
            if (!string.IsNullOrWhiteSpace(visionResult) && visionResult.Length > 20)
            {
                _logger.LogInformation("👁️ [{Agent}] Saving OCR result to knowledge base...", AgentName);
                var fileName = System.IO.Path.GetFileName(imagePath);
                await _ragService.IngestDocumentAsync(tenantId, $"OCR_{fileName}", visionResult);
                tools.Add("IngestToKnowledgeBase");
            }

            sw.Stop();
            return new AgentExecutionResult
            {
                AgentName = AgentName,
                Success = true,
                ResultContent = $"[Vision/OCR Result for {System.IO.Path.GetFileName(imagePath)}]: {visionResult}",
                ToolsUsed = tools,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "👁️ [{Agent}] Error during vision processing", AgentName);
            return AgentExecutionResult.Fail(AgentName, ex.Message);
        }
    }
}
