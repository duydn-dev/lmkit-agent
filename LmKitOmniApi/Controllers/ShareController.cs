using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LmKitOmniApi.Application.Share.Commands;
using LmKitOmniApi.Application.Share.Queries;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Read-only share links for chat sessions. Owners mint and revoke links for their
/// own sessions (tenant + user scoped in the handlers); the one anonymous endpoint
/// resolves a token to a public transcript. Ownership failures always surface as 404
/// — never 403 — so nothing is leaked about foreign sessions, and unknown vs revoked
/// tokens are deliberately indistinguishable.
/// </summary>
[ApiController]
[Route("api/share")]
[Authorize]
public sealed class ShareController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public ShareController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Rotate the share link for an owned session: any active links are revoked and a
    /// fresh token is minted. The raw token appears only in this response body.
    /// </summary>
    [HttpPost("chat-sessions/{sessionId:guid}")]
    public async Task<IActionResult> CreateShareLink(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var command = new CreateShareLinkCommand { SessionId = sessionId, TenantId = tenantId, UserId = userId };
        var token = await _mediator.Send(command, cancellationToken);
        return token is null ? NotFound() : Ok(new { token });
    }

    /// <summary>Revoke every active share link for an owned session.</summary>
    [HttpDelete("chat-sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeShareLinks(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var command = new RevokeShareLinksCommand { SessionId = sessionId, TenantId = tenantId, UserId = userId };
        var revoked = await _mediator.Send(command, cancellationToken);
        return revoked ? NoContent() : NotFound();
    }

    /// <summary>Public read-only transcript for a valid, unrevoked share token.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("SharePolicy")]
    [HttpGet("chat/{token}")]
    public async Task<IActionResult> GetSharedChat(string token, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSharedChatQuery { Token = token }, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
