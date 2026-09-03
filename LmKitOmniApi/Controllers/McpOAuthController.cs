using System.Text.Encodings.Web;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Per-user OAuth 2.0 authorization-code connect flow for MCP servers (RFC 6749 §4.1 with
/// PKCE, RFC 7636). <c>GET {serverId}/authorize</c> mints a PKCE challenge + CSRF state,
/// binds them server-side to the authenticated user, and returns the provider authorize URL
/// for the client to open. <c>GET callback</c> validates the state, exchanges the code, and
/// stores the encrypted per-user token. Every external URL (authorize + token) is SSRF-gated.
/// </summary>
[ApiController]
[Route("api/mcp-oauth")]
[Authorize]
public sealed class McpOAuthController : ApiControllerBase
{
    private readonly HermesDbContext _db;
    private readonly McpOAuthStateStore _stateStore;
    private readonly IMcpOAuthTokenProvider _tokenProvider;
    private readonly IMcpUserTokenStore _userTokenStore;
    private readonly ToolSandboxService _sandbox;
    private readonly ILogger<McpOAuthController> _logger;

    public McpOAuthController(
        HermesDbContext db,
        McpOAuthStateStore stateStore,
        IMcpOAuthTokenProvider tokenProvider,
        IMcpUserTokenStore userTokenStore,
        ToolSandboxService sandbox,
        ILogger<McpOAuthController> logger)
    {
        _db = db;
        _stateStore = stateStore;
        _tokenProvider = tokenProvider;
        _userTokenStore = userTokenStore;
        _sandbox = sandbox;
        _logger = logger;
    }

    /// <summary>
    /// Builds the provider authorize URL for the current user to open (in a popup/new tab)
    /// and persists the PKCE verifier + state binding under a short TTL.
    /// </summary>
    [HttpGet("{serverId:guid}/authorize")]
    public async Task<IActionResult> Authorize(Guid serverId, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var server = await _db.ExternalMcpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serverId && s.TenantId == tenantId, ct);
        if (server is null) return NotFound();
        if (!string.Equals(server.AuthMode, McpOAuthTokenProvider.AuthorizationCodeMode, StringComparison.OrdinalIgnoreCase))
            return BadRequest("MCP server is not configured for OAuth authorization-code.");
        if (string.IsNullOrWhiteSpace(server.OAuthAuthorizeUrl) ||
            string.IsNullOrWhiteSpace(server.OAuthTokenUrl) ||
            string.IsNullOrWhiteSpace(server.OAuthClientId))
            return BadRequest("MCP server is missing OAuth authorization-code configuration.");

        // Re-vet both endpoints at connect time (defense in depth; also gated at save time).
        var authorizeGate = await _sandbox.ValidateUrlAsync(server.OAuthAuthorizeUrl, ct);
        if (!authorizeGate.IsAllowed) return BadRequest(authorizeGate.DenialReason ?? "Authorize endpoint blocked.");
        var tokenGate = await _sandbox.ValidateUrlAsync(server.OAuthTokenUrl, ct);
        if (!tokenGate.IsAllowed) return BadRequest(tokenGate.DenialReason ?? "Token endpoint blocked.");

        var verifier = McpPkce.CreateVerifier();
        var challenge = McpPkce.Challenge(verifier);
        var redirectUri = BuildCallbackUrl();
        var state = await _stateStore.CreateAsync(
            new McpOAuthStateEntry(tenantId, userId, serverId, verifier, redirectUri, default), ct);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = server.OAuthClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (!string.IsNullOrWhiteSpace(server.OAuthScopes)) query["scope"] = server.OAuthScopes;

        var url = QueryHelpers.AddQueryString(server.OAuthAuthorizeUrl!, query);
        return Ok(new { url });
    }

    /// <summary>
    /// OAuth redirect target. Anonymous by design: the security is the single-use, server-
    /// bound <c>state</c> (which carries the tenant/user/server + PKCE verifier), not a
    /// session cookie — a cross-site provider redirect cannot be relied on to carry one. When
    /// a session IS present it is cross-checked against the state's user as defense in depth.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return ConnectedPage(false, "Authorization was denied by the provider.");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return ConnectedPage(false, "The authorization response was missing its code or state.");

        var entry = await _stateStore.ConsumeAsync(state, ct);
        if (entry is null)
            return ConnectedPage(false, "This authorization session has expired or was already used. Please try connecting again.");

        // Defense in depth: if the browser did carry a session, it must be the same user.
        if (TryGetUserId(out var sessionUserId) && sessionUserId != entry.UserId)
            return ConnectedPage(false, "This authorization link belongs to a different account.");

        var server = await _db.ExternalMcpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == entry.ServerId && s.TenantId == entry.TenantId, ct);
        if (server is null || string.IsNullOrWhiteSpace(server.OAuthTokenUrl))
            return ConnectedPage(false, "The MCP server is no longer available.");

        var tokenGate = await _sandbox.ValidateUrlAsync(server.OAuthTokenUrl, ct);
        if (!tokenGate.IsAllowed)
            return ConnectedPage(false, "The OAuth token endpoint is not permitted.");

        try
        {
            await _tokenProvider.ExchangeAuthorizationCodeAsync(
                server, entry.TenantId, entry.UserId, code, entry.CodeVerifier, entry.RedirectUri, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🔗 [MCP-OAuth] Authorization-code exchange failed for '{Server}'", server.Name);
            return ConnectedPage(false, "Could not complete the connection. Please try again.");
        }

        _logger.LogInformation("🔗 [MCP-OAuth] User {User} connected MCP server '{Server}'", entry.UserId, server.Name);
        return ConnectedPage(true, $"Connected to {server.Name}. You can close this window.");
    }

    /// <summary>Whether the current user has a stored token for the server. Never returns the token.</summary>
    [HttpGet("{serverId:guid}/status")]
    public async Task<IActionResult> Status(Guid serverId, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var exists = await _db.ExternalMcpServers.AsNoTracking()
            .AnyAsync(s => s.Id == serverId && s.TenantId == tenantId, ct);
        if (!exists) return NotFound();

        var token = await _userTokenStore.GetAsync(tenantId, userId, serverId, ct);
        return Ok(new { connected = token is not null, expiresAtUtc = token?.ExpiresAtUtc });
    }

    /// <summary>Disconnects the current user from the server by deleting their stored token.</summary>
    [HttpDelete("{serverId:guid}/token")]
    public async Task<IActionResult> Disconnect(Guid serverId, CancellationToken ct)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        await _userTokenStore.DeleteAsync(tenantId, userId, serverId, ct);
        return NoContent();
    }

    private string BuildCallbackUrl() =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/mcp-oauth/callback";

    /// <summary>A minimal self-contained HTML page (no external resources) that reports the
    /// outcome and closes the popup. CSP-friendly for the popup context.</summary>
    private ContentResult ConnectedPage(bool success, string message)
    {
        var safeMessage = HtmlEncoder.Default.Encode(message);
        var title = success ? "Connected" : "Connection failed";
        var accent = success ? "#059669" : "#dc2626";
        var icon = success ? "&#10003;" : "&#10007;";
        var html = $@"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{title}</title>
<style>
  body {{ font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; background: #f8fafc; color: #0f172a;
          display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; }}
  .card {{ background: #fff; border: 1px solid #e2e8f0; border-radius: 16px; padding: 32px 40px; max-width: 420px;
           text-align: center; box-shadow: 0 10px 30px rgba(15,23,42,.08); }}
  .badge {{ width: 56px; height: 56px; border-radius: 9999px; display: inline-flex; align-items: center;
            justify-content: center; font-size: 28px; color: #fff; background: {accent}; margin-bottom: 16px; }}
  h1 {{ font-size: 18px; margin: 0 0 8px; }}
  p {{ font-size: 14px; color: #475569; margin: 0; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""badge"">{icon}</div>
    <h1>{title}</h1>
    <p>{safeMessage}</p>
  </div>
  <script>
    // Notify an opener (the admin page) then close the popup shortly after.
    try {{ if (window.opener) window.opener.postMessage({{ type: 'mcp-oauth', success: {(success ? "true" : "false")} }}, '*'); }} catch (e) {{}}
    setTimeout(function () {{ window.close(); }}, {(success ? "1200" : "3500")});
  </script>
</body>
</html>";
        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest
        };
    }
}
