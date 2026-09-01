using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Audit.Queries;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Read-only admin view over the tenant audit log (agent tool invocations and
/// other recorded activity). Admin-only and always tenant-scoped: the tenant id
/// is taken from the authenticated principal, never from the request, so one
/// tenant can never read another tenant's activity.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Roles = "Admin")]
public sealed class AuditController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public AuditController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? actorType,
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new GetAuditLogsQuery
        {
            TenantId = tenantId,
            ActorType = actorType,
            Action = action,
            EntityType = entityType,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Page = page,
            PageSize = pageSize
        }, ct);

        return Ok(result);
    }

    [HttpGet("facets")]
    public async Task<IActionResult> Facets(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new GetAuditFacetsQuery { TenantId = tenantId }, ct);
        return Ok(result);
    }
}
