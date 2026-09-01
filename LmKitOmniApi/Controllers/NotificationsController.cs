using LmKitOmniApi.Application.Notifications.Commands;
using LmKitOmniApi.Application.Notifications.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Read/acknowledge endpoints for the caller's notifications (<c>/api/notifications</c>).
/// Rows are written by background workers (document vectorization, scheduled tasks); this
/// controller never creates notifications. Owner-scoped: foreign ids are a 404, never a 403.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public NotificationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool? unreadOnly, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var notifications = await _mediator.Send(new ListNotificationsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            UnreadOnly = unreadOnly == true
        }, ct);
        return Ok(notifications);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var marked = await _mediator.Send(new MarkNotificationReadCommand
        {
            TenantId = tenantId,
            UserId = userId,
            NotificationId = id
        }, ct);

        if (!marked) return NotFound();
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        await _mediator.Send(new MarkAllNotificationsReadCommand { TenantId = tenantId, UserId = userId }, ct);
        return NoContent();
    }
}
