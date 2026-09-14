using LmKitOmniApi.Services;
using System.Text.Json;
using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Application.AgentRuns.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Agent mode: goal-oriented autonomous runs on the shared ReAct orchestrator.
/// POST streams the run (SSE, same marker channel as chat plus [STEP:] and a
/// leading [AGENT_RUN:{id}]); GET endpoints list/read the caller's own runs with
/// their persisted step timeline. Per-user scoped — never admin-only.
/// </summary>
[ApiController]
[Route("api/agent-runs")]
[Authorize]
public sealed class AgentRunsController : ApiControllerBase
{
    private const int MaxGoalChars = 4000;

    private readonly IMediator _mediator;
    private readonly ILogger<AgentRunsController> _logger;

    public AgentRunsController(IMediator mediator, ILogger<AgentRunsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public sealed record StartAgentRunRequest(string Goal, Guid? CustomAgentId = null);

    [HttpPost]
    [EnableRateLimiting("ai-agent")]
    public async Task Start([FromBody] StartAgentRunRequest request, CancellationToken cancellationToken)
    {
        var goal = request?.Goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > MaxGoalChars)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new { message = $"Mục tiêu là bắt buộc và tối đa {MaxGoalChars} ký tự." }, cancellationToken);
            return;
        }

        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var command = new StreamAgentRunCommand
        {
            Goal = goal,
            TenantId = tenantId,
            UserId = userId,
            CustomAgentId = request.CustomAgentId
        };
        await StreamResponseAsync(_mediator.CreateStream(command, cancellationToken), cancellationToken);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? search, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var (p, size) = Application.Common.Paging.Normalize(page, pageSize);
        var runs = await _mediator.Send(new GetAgentRunsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            Page = p,
            PageSize = size,
            Search = search
        }, ct);
        return Ok(runs);
    }

    /// <summary>
    /// Hủy run đang đỗ (chờ phê duyệt / chờ lượt resume / mồ côi). 204 khi hủy được;
    /// 409 khi run đã kết thúc hoặc đang stream sống (dừng stream bằng cách ngắt kết
    /// nối SSE phía client); 404 khi không thuộc về người gọi.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var outcome = await _mediator.Send(new CancelAgentRunCommand
        {
            RunId = id,
            TenantId = tenantId,
            UserId = userId
        }, ct);
        return outcome switch
        {
            CancelAgentRunOutcome.Cancelled => NoContent(),
            CancelAgentRunOutcome.NotFound => NotFound(),
            CancelAgentRunOutcome.AlreadyFinished => Conflict(new { message = "Run đã kết thúc — không có gì để hủy." }),
            _ => Conflict(new { message = "Run đang stream trực tiếp — hãy dừng bằng cách ngắt kết nối stream (nút Dừng trên màn hình đang chạy)." })
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var run = await _mediator.Send(new GetAgentRunQuery { RunId = id, TenantId = tenantId, UserId = userId }, ct);
        return run is null ? NotFound() : Ok(run);
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
        catch (InferenceQueueRejectedException ex)
        {
            // Capacity, not a failure of the run or the server: the deployment's single chat
            // permit stayed busy past the queue's bound. The run is already recorded as Failed
            // with this same reason; the person watching gets the reason too, rather than the
            // generic error below.
            _logger.LogWarning(
                "Agent run turned away by the {Gate} inference queue ({Reason}) after {Waited}.",
                ex.Gate, ex.Reason, ex.Waited);
            await WriteSseAsync($"⚠️ {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run stream failed after response headers were sent");
            await WriteSseAsync("[ERROR]: Không thể thực thi agent run.", ct);
        }

        await WriteSseAsync("[DONE]", ct);
    }

    private async Task WriteSseAsync(string data, CancellationToken ct)
    {
        // JSON-encode so newlines/control chars stay in one event and tool output
        // can't inject fake SSE fields (identical to ChatController).
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
