using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// Fetches (and caches) OAuth 2.0 bearer tokens for MCP servers configured with the
/// client-credentials grant (RFC 6749 §4.4). The returned token is injected as an
/// <c>Authorization: Bearer</c> header on outbound MCP requests.
/// </summary>
public interface IMcpOAuthTokenProvider
{
    /// <summary>
    /// Returns a valid access token for <paramref name="server"/>, fetching a fresh one
    /// only when the cache is empty or the cached token is within the refresh skew of
    /// expiry. Throws <see cref="InvalidOperationException"/> when the server is not
    /// configured for client-credentials, the token endpoint is blocked by the SSRF
    /// sandbox, or the token request fails.
    /// </summary>
    Task<string> GetAccessTokenAsync(ExternalMcpServer server, CancellationToken ct = default);

    /// <summary>
    /// Returns the per-user access token for a server using the OAuth 2.0 authorization-code
    /// grant (RFC 6749 §4.1). Reads the token persisted for (<paramref name="tenantId"/>,
    /// <paramref name="userId"/>, server); when it is within 30s of expiry and a refresh
    /// token exists, transparently refreshes via <c>grant_type=refresh_token</c> and
    /// persists the result. Throws <see cref="InvalidOperationException"/> when the user has
    /// not connected the server, the token has expired with no way to refresh, or the token
    /// endpoint is blocked/failing.
    /// </summary>
    Task<string> GetUserAccessTokenAsync(ExternalMcpServer server, Guid tenantId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Exchanges an authorization <paramref name="code"/> (plus the PKCE
    /// <paramref name="codeVerifier"/> and the exact <paramref name="redirectUri"/> used in
    /// the authorize request) for tokens at the server's token endpoint, then persists the
    /// encrypted per-user token. SSRF-gates the token endpoint. Throws on failure.
    /// </summary>
    Task ExchangeAuthorizationCodeAsync(
        ExternalMcpServer server,
        Guid tenantId,
        Guid userId,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default);
}
