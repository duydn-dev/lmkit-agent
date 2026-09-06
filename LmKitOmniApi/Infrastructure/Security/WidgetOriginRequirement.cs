using System.Text.Json;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Authorization requirement+handler for the public widget surface: the caller
/// must be widget-authenticated (WidgetToken scheme) AND the effective request
/// origin (X-Widget-Origin, else Origin/Referer) must exactly match BOTH the
/// mint-time origin claim and a live allowlist entry of the caller's ACTIVE
/// TenantWidgetSettings row. Fail-closed: no settings row, disabled widget,
/// empty allowlist, missing origin or any mismatch → 403. Native callers could
/// forge the header, which is out of scope for the browser-embed threat model
/// (they could equally call the key exchange directly) and is still logged.
/// </summary>
public sealed class WidgetOriginRequirement : IAuthorizationRequirement;

public sealed class WidgetOriginAuthorizationHandler(
    HermesDbContext db,
    ILogger<WidgetOriginAuthorizationHandler> logger)
    : AuthorizationHandler<WidgetOriginRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WidgetOriginRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            context.Fail(new AuthorizationFailureReason(this, "Widget policy requires an HttpContext resource."));
            return;
        }

        var tenantRaw = context.User.FindFirst("TenantId")?.Value;
        if (!Guid.TryParse(tenantRaw, out var tenantId))
        {
            context.Fail(new AuthorizationFailureReason(this, "Widget principal lacks a tenant claim."));
            return;
        }

        var requestOrigin = ResolveRequestOrigin(http.Request);
        if (requestOrigin is null)
        {
            context.Fail(new AuthorizationFailureReason(this, "Widget request is missing an Origin/Referer."));
            return;
        }

        // Live read of ACTIVE settings: a disable or allowlist change applies
        // immediately without waiting for token expiry.
        TenantWidgetSettings? settings;
        try
        {
            settings = await db.TenantWidgetSettings.AsNoTracking()
                .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.IsActive, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Widget settings lookup failed for tenant {TenantId}; denying.", tenantId);
            context.Fail(new AuthorizationFailureReason(this, "Widget settings lookup failed."));
            return;
        }

        if (settings is null)
        {
            context.Fail(new AuthorizationFailureReason(this, "Widget is not active for this tenant."));
            return;
        }

        var allowed = WidgetOrigins.Parse(settings.AllowedOriginsJson);
        if (allowed.Count == 0
            || !allowed.Contains(requestOrigin, StringComparer.Ordinal)
            || !string.Equals(requestOrigin, context.User.FindFirst(WidgetAccessTokenService.OriginClaimType)?.Value, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Widget origin denied for tenant {TenantId}: request origin {RequestOrigin} not allowed.",
                tenantId, requestOrigin);
            context.Fail(new AuthorizationFailureReason(this, "Origin is not allowed."));
            return;
        }

        http.Items["Widget.RequestOrigin"] = requestOrigin;
        context.Succeed(requirement);
    }

    /// <summary>
    /// Effective widget origin (scheme+host[:port], lowercase). The widget app runs
    /// in a SAME-ORIGIN iframe, so browser fetches carry our origin — the embedded
    /// page's origin instead travels in <c>X-Widget-Origin</c>, computed inside the
    /// iframe from <c>window.location.ancestorOrigins</c> / <c>document.referrer</c>
    /// (a parent page cannot forge either for a cross-origin child). Falls back to
    /// the raw Origin/Referer headers for direct browser calls.
    /// </summary>
    public static string? ResolveRequestOrigin(HttpRequest request)
    {
        var raw = request.Headers["X-Widget-Origin"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) raw = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            var referer = request.Headers.Referer.ToString();
            if (string.IsNullOrWhiteSpace(referer)) return null;
            raw = referer;
        }
        // Origin may be "null" (opaque) or a list ("a, b") — never trust either.
        if (string.Equals(raw.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return null;
        var first = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
        if (!Uri.TryCreate(first, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}".ToLowerInvariant();
    }
}
