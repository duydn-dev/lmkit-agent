using System.Text.Json;
using LmKitOmniApi.Services;
using System.Threading.RateLimiting;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// PUBLIC widget surface (anonymous, cookie-free). Two steps:
/// 1) <c>POST /api/widget/auth</c> — exchange the raw widget key (X-Widget-Key)
///    for a short-lived widget token. Origin allowlist is enforced here.
/// 2) <c>POST /api/widget/chat</c> (SSE) — widget-token-authenticated, origin
///    re-checked live against ACTIVE settings, per-tenant+origin quota enforced
///    (local + Redis when available), direct-inference answer (no tools).
/// Auth/model selection stays server-controlled; the caller can only supply a
/// bounded text message.
/// </summary>
[ApiController]
[Route("api/widget")]
public sealed class WidgetPublicController(
    WidgetSettingsLookup settingsLookup,
    WidgetAccessTokenService tokenService,
    IWidgetChatEngine chatEngine,
    WidgetQuotaService quotaService,
    IConfiguration configuration,
    ILogger<WidgetPublicController> logger) : ControllerBase
{
    private const string RateLimitPolicyName = "widget-chat";

    /// <summary>
    /// Per-IP fixed-window policy for the ANONYMOUS key exchange, mirroring the
    /// LoginPolicy/SharePolicy shape used by the other unauthenticated endpoints.
    /// The exchange is a credential-presenting endpoint that drives an indexed
    /// key-hash lookup, so it must be throttled before it reaches the database.
    /// Registered in <c>Program.cs</c> alongside the other rate-limit policies.
    /// </summary>
    public const string AuthRateLimitPolicyName = "widget-auth";

    public sealed class WidgetAuthRequest
    {
        public string? Origin { get; init; }
    }

    public sealed class WidgetChatRequest
    {
        public string? Message { get; init; }
        public List<WidgetHistoryTurn>? History { get; init; }
    }

    [HttpPost("auth")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicyName)]
    public async Task<IActionResult> ExchangeKey([FromBody] WidgetAuthRequest? request, CancellationToken ct)
    {
        // Effective origin: X-Widget-Origin (set inside the same-origin iframe from
        // ancestorOrigins/referrer), else the raw Origin/Referer headers.
        var origin = WidgetOriginAuthorizationHandler.ResolveRequestOrigin(Request);
        if (origin is null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Thiếu Origin/Referer hợp lệ." });

        if (!Request.Headers.TryGetValue(WidgetSecrets.HeaderName, out var presented) || string.IsNullOrWhiteSpace(presented))
            return Unauthorized(new { message = "Thiếu khóa widget." });

        var rawKey = presented.ToString();
        if (rawKey.Length > WidgetSecrets.MaxPresentedLength)
            return Unauthorized(new { message = "Khóa widget không hợp lệ." });

        // Resolve the ACTIVE tenant by key-hash: unknown key, disabled widget and
        // rotated-away keys are all the same indistinguishable 401.
        var settings = await settingsLookup.FindActiveByKeyHashAsync(WidgetSecrets.Hash(rawKey), ct);
        if (settings is null)
            return Unauthorized(new { message = "Khóa widget không hợp lệ hoặc widget chưa được bật." });

        var allowed = WidgetOrigins.Parse(settings.AllowedOriginsJson);
        if (allowed.Count == 0 || !allowed.Contains(origin, StringComparer.Ordinal))
        {
            logger.LogWarning("Widget key exchange denied for tenant {TenantId}: origin {Origin} not allowlisted.", settings.TenantId, origin);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Origin không được phép." });
        }

        var token = tokenService.Mint(settings.TenantId, origin, DateTime.UtcNow);
        return Ok(new
        {
            accessToken = token,
            expiresInMinutes = configuration.GetValue("Widget:TokenLifetimeMinutes", 20),
            widget = new
            {
                settings.WidgetTitle,
                settings.WelcomeMessage,
                settings.BrandColor,
                settings.Position
            }
        });
    }

    /// <summary>
    /// Per-tenant framing policy for the widget DOCUMENT route
    /// (<c>GET /widget/chat?key=…</c>). The document itself is a static SPA file
    /// served by nginx, which cannot know a tenant's allowlist — so nginx issues
    /// this as an internal <c>auth_request</c> subrequest and copies the returned
    /// <c>frame-ancestors</c> onto the document response (see the widget location
    /// block in <c>LmKitOmniClient/nginx.conf</c>). The value is derived ONLY from
    /// the tenant's configured origin allowlist; an unknown key, a disabled widget
    /// or an empty allowlist all yield <c>'none'</c>, so the widget stays
    /// unembeddable until an admin explicitly allowlists an origin (fail closed).
    /// Deliberately un-throttled: it is one indexed key-hash read on the path of a
    /// page load, and a 429 here would blank the widget for every visitor sharing
    /// the proxy's address.
    /// </summary>
    [HttpGet("frame-policy")]
    [AllowAnonymous]
    public async Task<IActionResult> FramePolicy([FromQuery] string? key, CancellationToken ct)
    {
        var frameAncestors = await ResolveFrameAncestorsAsync(key, ct);

        // Header for nginx to lift onto the document (auth_request_set), plus the
        // real CSP so a direct fetch of this endpoint is self-describing.
        Response.Headers["X-Widget-Frame-Ancestors"] = frameAncestors;
        Response.Headers.ContentSecurityPolicy = $"frame-ancestors {frameAncestors}";
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    private async Task<string> ResolveFrameAncestorsAsync(string? rawKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length > WidgetSecrets.MaxPresentedLength)
            return "'none'";

        var settings = await settingsLookup.FindActiveByKeyHashAsync(WidgetSecrets.Hash(rawKey), ct);
        if (settings is null) return "'none'";

        // Re-normalize on the way out: the CSP header must never carry anything but
        // scheme://host[:port] tokens, whatever the stored JSON happens to hold.
        var allowed = WidgetOrigins.Parse(settings.AllowedOriginsJson)
            .Select(WidgetOrigins.Normalize)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return allowed.Count == 0 ? "'none'" : string.Join(' ', allowed);
    }

    [HttpPost("chat")]
    [Authorize(AuthenticationSchemes = WidgetTokenAuthenticationHandler.SchemeName)]
    [Authorize(Policy = "WidgetOrigin")]
    [EnableRateLimiting(RateLimitPolicyName)]
    public async Task<IActionResult> Chat([FromBody] WidgetChatRequest? request, CancellationToken ct)
    {
        // Origin is authoritative from the policy (stored in HttpContext.Items);
        // do NOT trust a body field.
        if (!HttpContext.Items.TryGetValue("Widget.RequestOrigin", out var originObj) || originObj is not string origin)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Origin không được phép." });

        if (!TryGetWidgetTenantId(out var tenantId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Widget principal không hợp lệ." });

        var settings = await LoadEnabledSettingsAsync(ct);
        if (settings is null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Widget chưa được bật." });

        var message = request?.Message?.Trim() ?? string.Empty;
        if (message.Length is 0 or > WidgetChatEngine.MaxMessageCharacters)
            return BadRequest(new { message = $"Câu hỏi phải chứa từ 1 đến {WidgetChatEngine.MaxMessageCharacters} ký tự." });

        var history = SanitizeHistory(request?.History);
        if (history.Count > WidgetChatEngine.MaxHistoryMessages)
            history = history.TakeLast(WidgetChatEngine.MaxHistoryMessages).ToList();

        if (!await quotaService.TryConsumeAsync(
                tenantId, origin,
                settings.RequestsPerMinute, settings.RequestsPerDay, ct))
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = "Widget đã vượt giới hạn lượt trò chuyện. Vui lòng thử lại sau." });
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.StatusCode = StatusCodes.Status200OK;

        try
        {
            await foreach (var chunk in chatEngine.StreamAnswerAsync(
                new WidgetTurnRequest(tenantId, message, history), ct))
            {
                await Response.WriteAsync("data: ", ct);
                await Response.WriteAsync(chunk, ct);
                await Response.WriteAsync("\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected mid-stream — nothing to persist (widget never
            // persists anything), so just stop writing.
        }
        catch (InferenceQueueRejectedException refused) when (Response.HasStarted)
        {
            // The chat permit is taken inside OpenAsync, BEFORE the first chunk, so today a
            // refusal surfaces before the response starts and the global handler answers it
            // as a 503. This branch is the guard for the day that ordering changes: a stream
            // that has already started cannot become a 503, and ending it silently left the
            // embedded widget showing nothing at all.
            await Response.WriteAsync("data: ", ct);
            await Response.WriteAsync(JsonSerializer.Serialize(new { done = true, answer = refused.Message }), ct);
            await Response.WriteAsync("\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        return new EmptyResult();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<Domain.Entities.TenantWidgetSettings?> LoadEnabledSettingsAsync(CancellationToken ct)
    {
        if (!TryGetWidgetTenantId(out var tenantId)) return null;
        return await settingsLookup.GetActiveAsync(tenantId, ct);
    }

    private bool TryGetWidgetTenantId(out Guid tenantId)
    {
        tenantId = default;
        var raw = User.FindFirst("TenantId")?.Value;
        return Guid.TryParse(raw, out tenantId)
            && string.Equals(User.FindFirst("auth_method")?.Value, WidgetTokenAuthenticationHandler.AuthMethodClaimValue, StringComparison.Ordinal);
    }

    private static List<WidgetHistoryTurn> SanitizeHistory(List<WidgetHistoryTurn>? history)
    {
        if (history is null || history.Count == 0) return [];
        var sanitized = new List<WidgetHistoryTurn>(history.Count);
        foreach (var turn in history)
        {
            if (turn is null) continue;
            var role = turn.Role?.Trim().ToLowerInvariant();
            if (role is not ("user" or "assistant")) continue;
            var content = turn.Content?.Trim() ?? string.Empty;
            if (content.Length == 0) continue;
            if (content.Length > WidgetChatEngine.MaxMessageCharacters)
                content = content[..WidgetChatEngine.MaxMessageCharacters];
            sanitized.Add(new WidgetHistoryTurn(role, content));
        }
        return sanitized;
    }

}
