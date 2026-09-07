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
/// — never 403 — so nothing is leaked about foreign sessions.
///
/// <para>The public read has three outcomes, not two: <b>200</b> with the transcript,
/// <b>410 Gone</b> for a link that existed and no longer resolves (carrying a
/// <c>reason</c> of <c>"revoked"</c> or <c>"expired"</c>), and <b>404</b> for everything
/// unknown. Both 410s hand back zero transcript bytes and travel the same code path —
/// they are refused identically; they merely say which clock ran out. The reasoning for
/// splitting them, and for keeping 404 opaque, is on
/// <see cref="SharedChatStatus"/>.</para>
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
    /// fresh token is minted. The raw token appears only in this response body, next to
    /// the deadline the owner has to re-share before.
    /// </summary>
    [HttpPost("chat-sessions/{sessionId:guid}")]
    public async Task<IActionResult> CreateShareLink(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var command = new CreateShareLinkCommand { SessionId = sessionId, TenantId = tenantId, UserId = userId };
        var created = await _mediator.Send(command, cancellationToken);
        return created is null
            ? NotFound()
            : Ok(new { token = created.Token, expiresAtUtc = created.ExpiresAtUtc });
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

    /// <summary>
    /// Public read-only transcript for a share token that is known, unrevoked and
    /// unexpired. 410 Gone names which of the last two failed; 404 says nothing.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("SharePolicy")]
    [HttpGet("chat/{token}")]
    public async Task<IActionResult> GetSharedChat(string token, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSharedChatQuery { Token = token }, cancellationToken);
        return result.Status switch
        {
            SharedChatStatus.Ok => Ok(result.Chat),
            // 410 rather than 404 because RFC 9110's Gone is exactly this situation: the
            // resource genuinely existed at this URL and the condition is permanent, so a
            // client, crawler or cache should stop retrying instead of treating it as a
            // possibly-mistyped path.
            SharedChatStatus.Revoked => StatusCode(
                StatusCodes.Status410Gone,
                new { reason = "revoked", revokedAtUtc = result.RefusedAtUtc }),
            SharedChatStatus.Expired => StatusCode(
                StatusCodes.Status410Gone,
                new { reason = "expired", expiredAtUtc = result.RefusedAtUtc }),
            _ => NotFound()
        };
    }
}
