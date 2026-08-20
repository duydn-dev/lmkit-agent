using System.Diagnostics;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Vision Agent — specialized in OCR, image understanding, chat-with-image.
/// Read-only vision specialist. Knowledge ingestion is deliberately handled by
/// an explicit upload/ingestion workflow so delegation cannot bypass approval.
/// </summary>
public class VisionAgent : ISpecializedAgent
{
    private readonly IMediator _mediator;
    private readonly UserResourceAccessService _resources;
    private readonly AgentToolGateway _toolGateway;
    private readonly ILogger<VisionAgent> _logger;

    public string AgentName => "VisionAgent";
    public string Description => "Chuyên xử lý hình ảnh: OCR, nhận dạng nội dung và trích xuất text từ ảnh/PDF trong vùng file được phép.";
    public IReadOnlyList<string> SupportedCategories => new[] { "vision", "ocr", "image", "photo", "picture" };

    public VisionAgent(
        IMediator mediator,
        UserResourceAccessService resources,
        AgentToolGateway toolGateway,
        ILogger<VisionAgent> logger)
    {
        _mediator = mediator;
        _resources = resources;
        _toolGateway = toolGateway;
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

    public async Task<AgentExecutionResult> ExecuteAsync(Guid tenantId, Guid? userId, string userRole, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var tools = new List<string>();

        try
        {
            // Extract image path from query
            var pathMatch = System.Text.RegularExpressions.Regex.Match(
                query,
                "(?:\\\"(?<path>[^\\\"]+\\.(?:jpg|jpeg|png|bmp|webp))\\\"|'(?<path>[^']+\\.(?:jpg|jpeg|png|bmp|webp))'|(?<path>\\S+\\.(?:jpg|jpeg|png|bmp|webp)))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var imagePath = pathMatch.Success ? pathMatch.Groups["path"].Value : null;

            if (string.IsNullOrEmpty(imagePath))
            {
                sw.Stop();
                return AgentExecutionResult.Fail(AgentName, "Không tìm thấy đường dẫn hình ảnh trong yêu cầu.");
            }

            if (userId is null)
                return AgentExecutionResult.Fail(AgentName, "A user identity is required for image access.");

            var pathValidation = _resources.ValidateOwnedPath(tenantId, userId.Value, imagePath.Trim('"', '\''));
            if (!pathValidation.IsAllowed)
            {
                sw.Stop();
                return AgentExecutionResult.Fail(AgentName, pathValidation.DenialReason ?? "Đường dẫn hình ảnh không được phép.");
            }

            // Step 1: Analyze image (OCR / VLM)
            _logger.LogInformation("👁️ [{Agent}] Analyzing validated image: {Path}", AgentName, pathValidation.SanitizedPath);
            var execution = await _toolGateway.ExecuteReadOnlyAsync(
                tenantId, userId, userRole, "AnalyzeImage", pathValidation.SanitizedPath,
                token => _mediator.Send(
                    new AnalyzeImageCommand { ImagePath = pathValidation.SanitizedPath }, token), ct);

            if (!execution.IsSuccess)
                return AgentExecutionResult.Fail(AgentName, execution.ErrorMessage ?? "Image analysis failed.");

            var visionResult = execution.Output;
            tools.Add("AnalyzeImage");

            sw.Stop();
            return new AgentExecutionResult
            {
                AgentName = AgentName,
                Success = true,
                ResultContent = $"[Vision/OCR Result for {System.IO.Path.GetFileName(pathValidation.SanitizedPath)}]: {visionResult}",
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
