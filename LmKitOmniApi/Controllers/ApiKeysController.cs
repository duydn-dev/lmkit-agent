using LmKitOmniApi.Application.ApiKeys;
using LmKitOmniApi.Application.ApiKeys.Commands;
using LmKitOmniApi.Application.ApiKeys.Queries;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Self-service API-key management, scoped to the calling tenant + user. Both
/// authentication schemes reach the endpoints via the default policy, but requests
/// authenticated BY an API key are refused here: a leaked key must never be able to
/// list, mint, or revoke keys. Only an interactive JWT session may manage keys.
/// </summary>
[ApiController]
[Route("api/api-keys")]
[Authorize]
public sealed class ApiKeysController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public ApiKeysController(IMediator mediator)
    {
        _mediator = mediator;
    }

    public sealed class CreateApiKeyRequest
    {
        public string? Name { get; init; }
        public int? ExpiresInDays { get; init; }
        public int? MaxRequests { get; init; }
    }

    /// <summary>
    /// GET /api/api-keys — the caller's keys, newest first. Never returns key
    /// material: only the SHA-256 hash is stored, so not even a prefix can be shown.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (RefuseApiKeyPrincipal() is { } refused) return refused;
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var keys = await _mediator.Send(new ListApiKeysQuery { TenantId = tenantId, UserId = userId }, ct);
        return Ok(keys);
    }

    /// <summary>
    /// POST /api/api-keys — mints a key and returns the raw secret exactly once.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (RefuseApiKeyPrincipal() is { } refused) return refused;
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new CreateApiKeyCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Name = request.Name,
            ExpiresInDays = request.ExpiresInDays,
            MaxRequests = request.MaxRequests
        }, ct);

        if (result.Status == ApiKeyMutationStatus.ValidationFailed)
            return BadRequest(new { message = result.ErrorMessage });

        return CreatedAtAction(nameof(List), result.Key);
    }

    /// <summary>
    /// DELETE /api/api-keys/{id} — revokes one of the caller's keys. Foreign or
    /// unknown ids are an indistinguishable 404.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (RefuseApiKeyPrincipal() is { } refused) return refused;
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var revoked = await _mediator.Send(new RevokeApiKeyCommand
        {
            TenantId = tenantId,
            UserId = userId,
            KeyId = id
        }, ct);
        if (!revoked) return NotFound();
        return NoContent();
    }

    /// <summary>
    /// 403 for principals authenticated by an API key (the <c>auth_method=api_key</c>
    /// marker claim), <c>null</c> for interactive JWT sessions.
    /// </summary>
    private ObjectResult? RefuseApiKeyPrincipal()
    {
        if (!User.HasClaim(ApiKeyAuthenticationHandler.AuthMethodClaimType, ApiKeyAuthenticationHandler.AuthMethodClaimValue))
            return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            message = "Không thể quản lý khóa API bằng yêu cầu xác thực qua khóa API. Vui lòng đăng nhập bằng phiên người dùng (JWT) để tạo, xem hoặc thu hồi khóa."
        });
    }
}
