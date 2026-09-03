using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>A decrypted per-user OAuth token as read back from the store.</summary>
public sealed record StoredUserToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc, string? Scope);

/// <summary>
/// Persists per-user OAuth 2.0 authorization-code tokens for MCP servers, scoped by
/// tenant + user + server and encrypted at rest via <see cref="McpHeaderProtector"/>.
/// There is at most one row per (tenant, user, server); a new grant or a refresh replaces
/// the stored tokens in place.
/// </summary>
public interface IMcpUserTokenStore
{
    Task<StoredUserToken?> GetAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default);

    Task SaveAsync(
        Guid tenantId,
        Guid userId,
        Guid serverId,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAtUtc,
        string? scope,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default);
}

public sealed class McpUserTokenStore : IMcpUserTokenStore
{
    private readonly HermesDbContext _db;
    private readonly McpHeaderProtector _protector;

    public McpUserTokenStore(HermesDbContext db, McpHeaderProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<StoredUserToken?> GetAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default)
    {
        var row = await _db.McpUserOAuthTokens.AsNoTracking().FirstOrDefaultAsync(
            t => t.TenantId == tenantId && t.UserId == userId && t.ServerId == serverId, ct);
        if (row is null) return null;

        var access = _protector.Unprotect(row.AccessTokenProtected);
        var refresh = string.IsNullOrEmpty(row.RefreshTokenProtected) ? null : _protector.Unprotect(row.RefreshTokenProtected);
        // Rows come back with an unspecified DateTimeKind on some providers (SQLite); the
        // column is always UTC, so pin the kind before projecting to a DateTimeOffset.
        var expires = new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAtUtc, DateTimeKind.Utc));
        return new StoredUserToken(access, refresh, expires, row.Scope);
    }

    public async Task SaveAsync(
        Guid tenantId,
        Guid userId,
        Guid serverId,
        string accessToken,
        string? refreshToken,
        DateTimeOffset expiresAtUtc,
        string? scope,
        CancellationToken ct = default)
    {
        var row = await _db.McpUserOAuthTokens.FirstOrDefaultAsync(
            t => t.TenantId == tenantId && t.UserId == userId && t.ServerId == serverId, ct);

        var now = DateTime.UtcNow;
        var accessProtected = _protector.Protect(accessToken);
        var refreshProtected = string.IsNullOrEmpty(refreshToken) ? null : _protector.Protect(refreshToken);
        var expiresUtc = expiresAtUtc.UtcDateTime;

        if (row is null)
        {
            _db.McpUserOAuthTokens.Add(new McpUserOAuthToken
            {
                TenantId = tenantId,
                UserId = userId,
                ServerId = serverId,
                AccessTokenProtected = accessProtected,
                RefreshTokenProtected = refreshProtected,
                ExpiresAtUtc = expiresUtc,
                Scope = scope,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        else
        {
            row.AccessTokenProtected = accessProtected;
            row.RefreshTokenProtected = refreshProtected;
            row.ExpiresAtUtc = expiresUtc;
            row.Scope = scope;
            row.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid userId, Guid serverId, CancellationToken ct = default)
    {
        var row = await _db.McpUserOAuthTokens.FirstOrDefaultAsync(
            t => t.TenantId == tenantId && t.UserId == userId && t.ServerId == serverId, ct);
        if (row is null) return false;
        _db.McpUserOAuthTokens.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
