using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Shared base for authenticated API controllers. Centralizes parsing of the
/// authoritative identity claims minted by <c>AuthController.GenerateJwtToken</c>
/// so every controller reads them the same way:
/// <list type="bullet">
///   <item>The user id is issued as the <c>sub</c> claim and surfaced by the JWT
///   handler as <see cref="ClaimTypes.NameIdentifier"/> (see the
///   <c>NameClaimType</c> configuration in Program.cs and the token validation
///   that fails when <see cref="ClaimTypes.NameIdentifier"/> is absent).</item>
///   <item>The tenant id travels in the custom <c>"TenantId"</c> claim.</item>
///   <item>The auth session id travels in the custom <c>"sid"</c> claim.</item>
/// </list>
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Parses both the tenant id and user id from the current principal.
    /// Returns <c>false</c> when either claim is missing or malformed.
    /// </summary>
    protected bool TryGetIdentity(out Guid tenantId, out Guid userId)
    {
        var tenantValid = Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);
        var userValid = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        return tenantValid && userValid;
    }

    /// <summary>Parses the tenant id (the <c>"TenantId"</c> claim) from the current principal.</summary>
    protected bool TryGetTenantId(out Guid tenantId) =>
        Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);

    /// <summary>Parses the user id (the mapped <c>sub</c> claim) from the current principal.</summary>
    protected bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

    /// <summary>Parses the auth session id (the <c>"sid"</c> claim) from the current principal.</summary>
    protected bool TryGetSessionId(out Guid sessionId) =>
        Guid.TryParse(User.FindFirst("sid")?.Value, out sessionId);
}
