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

    public sealed record StartAgentRunRequest(string Goal);

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

        var command = new StreamAgentRunCommand { Goal = goal, TenantId = tenantId, UserId = userId };
        await StreamResponseAsync(_mediator.CreateStream(command, cancellationToken), cancellationToken);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        var runs = await _mediator.Send(new GetAgentRunsQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(runs);
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
