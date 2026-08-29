using System.Text.Json;
using System.Text.RegularExpressions;
using LmKitOmniApi.Application.McpServers.Commands;
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
        return null;
    }

    internal static string? ProtectHeaders(McpHeaderProtector protector, Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        return protector.Protect(JsonSerializer.Serialize(headers));
    }
}
