using System.Text.Json;
using System.Text.RegularExpressions;
using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.McpServers;

/// <summary>
/// Validation and header-protection rules moved out of <c>McpServersController</c> so the
/// create and update handlers share the exact same checks, messages, and check order.
/// </summary>
internal static class McpServerRules
{
    private static readonly Regex ValidName = new("^[a-zA-Z0-9][a-zA-Z0-9_-]{1,63}$", RegexOptions.Compiled);
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Transfer-Encoding", "X-Forwarded-For", "X-Forwarded-Host", "Forwarded"
    };

    /// <summary>
    /// Returns null when the request is valid; otherwise a failure result carrying the exact
    /// error string the controller previously returned. Check order is unchanged:
    /// name format → absolute URL → SSRF sandbox → per-tenant name uniqueness → header limits.
    /// </summary>
    internal static async Task<SaveMcpServerResult?> ValidateAsync(
        HermesDbContext db,
        ToolSandboxService sandbox,
        Guid tenantId,
        Guid? id,
        SaveMcpServerCommandBase request,
        CancellationToken ct)
    {
        var normalizedName = request.Name?.Trim() ?? string.Empty;
        if (!ValidName.IsMatch(normalizedName))
            return SaveMcpServerResult.ValidationFailed("Name must contain 2-64 letters, digits, underscores or hyphens.");
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            return SaveMcpServerResult.ValidationFailed("A valid absolute URL is required.");
        var url = await sandbox.ValidateUrlAsync(request.Url, ct);
        if (!url.IsAllowed) return SaveMcpServerResult.ValidationFailed(url.DenialReason);
        var comparableName = normalizedName.ToLower();
        if (await db.ExternalMcpServers.AnyAsync(server => server.TenantId == tenantId && server.Name.ToLower() == comparableName && server.Id != id, ct))
            return SaveMcpServerResult.NameConflict("An MCP server with this name already exists.");
        if (request.Headers?.Count > 20 || request.Headers?.Any(header => BlockedHeaders.Contains(header.Key) || header.Key.Length > 100 || header.Value is null || header.Value.Length > 2_000) == true)
            return SaveMcpServerResult.ValidationFailed("MCP headers exceeded the allowed limits or contained a blocked header.");

        var authMode = NormalizeAuthMode(request.AuthMode);
        if (authMode is null)
            return SaveMcpServerResult.ValidationFailed("AuthMode must be 'Static', 'ClientCredentials' or 'AuthorizationCode'.");
        if (authMode is McpOAuthTokenProvider.ClientCredentialsMode or McpOAuthTokenProvider.AuthorizationCodeMode)
        {
            if (string.IsNullOrWhiteSpace(request.OAuthClientId) || request.OAuthClientId.Length > 300)
                return SaveMcpServerResult.ValidationFailed("OAuth client id is required (max 300 characters).");
            if (!Uri.TryCreate(request.OAuthTokenUrl, UriKind.Absolute, out _) || request.OAuthTokenUrl!.Length > 500)
                return SaveMcpServerResult.ValidationFailed("A valid absolute OAuth token URL is required (max 500 characters).");
            // Same SSRF sandbox as the server URL: the token endpoint must not resolve to internal space.
            var tokenUrl = await sandbox.ValidateUrlAsync(request.OAuthTokenUrl, ct);
            if (!tokenUrl.IsAllowed) return SaveMcpServerResult.ValidationFailed(tokenUrl.DenialReason);
            // The authorization-code grant additionally redirects the user's browser to an
            // authorize endpoint, which is SSRF-gated exactly like the token endpoint.
            if (authMode == McpOAuthTokenProvider.AuthorizationCodeMode)
            {
                if (!Uri.TryCreate(request.OAuthAuthorizeUrl, UriKind.Absolute, out _) || request.OAuthAuthorizeUrl!.Length > 500)
                    return SaveMcpServerResult.ValidationFailed("A valid absolute OAuth authorize URL is required (max 500 characters).");
                var authorizeUrl = await sandbox.ValidateUrlAsync(request.OAuthAuthorizeUrl, ct);
                if (!authorizeUrl.IsAllowed) return SaveMcpServerResult.ValidationFailed(authorizeUrl.DenialReason);
            }
            if (request.OAuthScopes is { Length: > 1_000 })
                return SaveMcpServerResult.ValidationFailed("OAuth scopes exceeded 1000 characters.");
            if (request.OAuthClientSecret is { Length: > 4_000 })
                return SaveMcpServerResult.ValidationFailed("OAuth client secret exceeded the allowed length.");
            // A brand-new OAuth server must carry a secret; on update a blank secret means
            // "keep the stored one", so the presence check lives in the handler.
            if (id is null && string.IsNullOrWhiteSpace(request.OAuthClientSecret))
                return SaveMcpServerResult.ValidationFailed("OAuth client secret is required.");
        }
        return null;
    }

    /// <summary>
    /// Canonicalizes the auth mode: blank → "Static", case-insensitive match to the two
    /// supported modes, otherwise null (invalid). Shared by validation and persistence so
    /// the stored value is always canonical.
    /// </summary>
    internal static string? NormalizeAuthMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "Static";
        if (string.Equals(mode, "Static", StringComparison.OrdinalIgnoreCase)) return "Static";
        if (string.Equals(mode, McpOAuthTokenProvider.ClientCredentialsMode, StringComparison.OrdinalIgnoreCase))
            return McpOAuthTokenProvider.ClientCredentialsMode;
        if (string.Equals(mode, McpOAuthTokenProvider.AuthorizationCodeMode, StringComparison.OrdinalIgnoreCase))
            return McpOAuthTokenProvider.AuthorizationCodeMode;
        return null;
    }

    internal static string ProtectSecret(McpHeaderProtector protector, string secret) => protector.Protect(secret);

    internal static string? ProtectHeaders(McpHeaderProtector protector, Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        return protector.Protect(JsonSerializer.Serialize(headers));
    }
}
