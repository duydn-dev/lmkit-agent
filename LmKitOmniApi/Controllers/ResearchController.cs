using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Research;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// DEEP RESEARCH endpoint — streams a multi-step research run over SSE using
/// the exact same event encoding as <see cref="ChatController"/>:
/// each event is <c>data: &lt;JSON-encoded string&gt;\n\n</c>, progress arrives as
/// <c>[THINKING]: ...</c> lines, the report as markdown chunks, then a
/// <c>[RESEARCH_SAVED:{rootId}]</c> marker, and finally <c>[DONE]</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ResearchController : ApiControllerBase
{
    private readonly DeepResearchService _deepResearch;
    private readonly ILogger<ResearchController> _logger;

    public ResearchController(DeepResearchService deepResearch, ILogger<ResearchController> logger)
    {
        _deepResearch = deepResearch;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/research — body { "query": string, "maxSources": int? }.
    /// Query is required (≤ 500 chars); maxSources is clamped to 2..5 (default 3).
    /// </summary>
    [Authorize]
    [EnableRateLimiting("ai-agent")] // same policy the chat stream uses
    [HttpPost]
    public async Task StartResearch([FromBody] StartResearchRequest request, CancellationToken cancellationToken)
    {
        var query = request?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length > ResearchLimits.MaxQueryChars)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new
            {
                message = $"Câu hỏi nghiên cứu là bắt buộc và không được vượt quá {ResearchLimits.MaxQueryChars} ký tự."
            }, cancellationToken);
            return;
        }

        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        var maxSources = Math.Clamp(
            request!.MaxSources ?? ResearchLimits.DefaultSources,
            ResearchLimits.MinSources,
            ResearchLimits.MaxSources);

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var stream = _deepResearch.RunAsync(tenantId, userId, query, maxSources, cancellationToken);
        await StreamResponseAsync(stream, cancellationToken);
    }

    private async Task StreamResponseAsync(IAsyncEnumerable<string> stream, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in stream.WithCancellation(ct))
                await WriteSseAsync(chunk, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Research stream failed after response headers were sent");
            await WriteSseAsync("[ERROR]: Unable to generate a response.", ct);
        }

        await WriteSseAsync("[DONE]", ct);
    }

    private async Task WriteSseAsync(string data, CancellationToken ct)
    {
        // JSON encoding keeps newlines and control characters inside one SSE
        // event and prevents model output from injecting fake SSE fields.
        var message = $"data: {JsonSerializer.Serialize(data)}\n\n";
        await Response.WriteAsync(message, ct);
        await Response.Body.FlushAsync(ct);
    }
}
