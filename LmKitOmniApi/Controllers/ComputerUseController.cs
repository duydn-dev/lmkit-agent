using System.Text.Json;
using LmKitOmniApi.Application.ComputerUse.Commands;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Interactive COMPUTER-USE agent: a goal + optional start URL drives a perception→action
/// loop (observe → decide → approve → act) in a locked browser container. The POST streams
/// the run over SSE — the same marker channel as chat plus <c>[STEP:]</c>, <c>[FILE:]</c>
/// (per-step screenshot) and <c>[HITL_APPROVAL_REQUIRED:{id}]</c> before every gated action
/// — and the two approval endpoints record the human's decision on the pending action.
///
/// Tenant-scoped and role-gated to Admin/User (never Guest). OFF BY DEFAULT: when the
/// tool is not enabled the stream endpoint returns 501 and nothing can launch. This
/// controller and its services work end-to-end without touching Program.cs / appsettings /
/// the orchestrator (the required DI + config wiring is documented in
/// COMPUTER-USE-INTEGRATION.md).
/// </summary>
[ApiController]
[Route("api/agent/computer-use")]
[Authorize]
public sealed class ComputerUseController : ApiControllerBase
{
    private const int MaxTaskChars = 4000;
    private const int MaxUrlChars = 2048;

    private readonly IComputerUseAgent _agent;
    private readonly IMediator _mediator;
    private readonly ILogger<ComputerUseController> _logger;

    public ComputerUseController(IComputerUseAgent agent, IMediator mediator, ILogger<ComputerUseController> logger)
    {
        _agent = agent;
        _mediator = mediator;
        _logger = logger;
    }

    public sealed record StartComputerUseRequest(string Task, string? StartUrl);
    public sealed record ResolveRequest(string? Comment);

    /// <summary>Streams an interactive computer-use run (SSE). 501 when the tool is disabled.</summary>
    [HttpPost]
    [EnableRateLimiting("ai-agent")]
    public async Task Run([FromBody] StartComputerUseRequest request, CancellationToken cancellationToken)
    {
        var task = request?.Task?.Trim() ?? string.Empty;
        var startUrl = request?.StartUrl?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(task) || task.Length > MaxTaskChars)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync($"Task is required and must be at most {MaxTaskChars} characters.", cancellationToken);
            return;
        }
        if (startUrl.Length > MaxUrlChars)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync($"startUrl must be at most {MaxUrlChars} characters.", cancellationToken);
            return;
        }

        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        // Role gate: Admin/User only, never Guest.
        var role = User.FindFirst("Role")?.Value ?? string.Empty;
        if (!IsAdminOrUser(role))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Response.WriteAsync("Computer-use is available to Admin and User roles only.", cancellationToken);
            return;
        }

        // OFF BY DEFAULT: refuse cleanly (501) before any streaming begins.
        if (!_agent.IsEnabled)
        {
            Response.StatusCode = StatusCodes.Status501NotImplemented;
            await Response.WriteAsync("Computer-use is not enabled on this server.", cancellationToken);
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var run = new ComputerUseRequest(
            tenantId, userId, Guid.NewGuid(),
            IsAdmin(role) ? "Admin" : "User",
            task, startUrl);

        await StreamResponseAsync(_agent.RunAsync(run, cancellationToken), cancellationToken);
    }

    /// <summary>Approves the pending computer-use action, letting the streaming loop proceed.</summary>
    [HttpPost("approvals/{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, CancellationToken ct) => ResolveAsync(id, approve: true, comment: null, ct);

    /// <summary>Rejects the pending computer-use action, stopping the loop.</summary>
    [HttpPost("approvals/{id:guid}/reject")]
    public Task<IActionResult> Reject(Guid id, [FromBody] ResolveRequest? request, CancellationToken ct)
        => ResolveAsync(id, approve: false, comment: request?.Comment, ct);

    private async Task<IActionResult> ResolveAsync(Guid id, bool approve, string? comment, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var outcome = await _mediator.Send(new ResolveComputerUseApprovalCommand
        {
            ApprovalId = id,
            TenantId = tenantId,
            UserId = userId,
            Approve = approve,
            Comment = comment,
        }, ct);

        return outcome switch
        {
            ResolveComputerUseApprovalOutcome.NotFound => NotFound("Computer-use approval not found."),
            ResolveComputerUseApprovalOutcome.Conflict => Conflict("Approval is no longer pending."),
            _ => Ok(new { Success = true, Approved = approve }),
        };
    }

    private static bool IsAdmin(string role) => role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdminOrUser(string role) =>
        role.Equals("Admin", StringComparison.OrdinalIgnoreCase)
        || role.Equals("User", StringComparison.OrdinalIgnoreCase);

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
            _logger.LogError(ex, "Computer-use stream failed after response headers were sent");
            await WriteSseAsync("[ERROR]: Không thể thực thi phiên computer-use.", ct);
        }

        await WriteSseAsync("[DONE]", ct);
    }

    private async Task WriteSseAsync(string data, CancellationToken ct)
    {
        // JSON-encode so newlines/control chars stay in one event and tool output can't
        // inject fake SSE fields (identical to ChatController / AgentRunsController).
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
