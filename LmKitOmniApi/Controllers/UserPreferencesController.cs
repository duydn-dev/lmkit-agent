using LmKitOmniApi.Application.UserPreferences.Commands;
using LmKitOmniApi.Application.UserPreferences.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// User-level custom instructions (ChatGPT-style): a per-user persona (AboutUser +
/// ResponseStyle) prepended to the system prompt of every chat the user starts.
/// Strictly self-scoped from the JWT claims — a caller can only ever read or write
/// their own row, so there is no id in the route and no cross-user access.
/// </summary>
[ApiController]
[Route("api/user/custom-instructions")]
[Authorize]
public sealed class UserPreferencesController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public UserPreferencesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>The caller's custom instructions; an all-null object when none saved.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var dto = await _mediator.Send(new GetUserPreferenceQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(dto);
    }

    /// <summary>Upserts the caller's custom instructions. 400 on a length violation.</summary>
    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpsertCustomInstructionsRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new UpsertUserPreferenceCommand
        {
            TenantId = tenantId,
            UserId = userId,
            AboutUser = request.AboutUser,
            ResponseStyle = request.ResponseStyle
        }, ct);

        return result.ErrorMessage is not null
            ? BadRequest(new { message = result.ErrorMessage })
            : Ok(result.Preferences);
    }
}
