using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Loads the ACTIVE widget settings for a tenant. <c>null</c> means the tenant
/// has no settings row or the widget is disabled — both states deny access
/// (fail closed) and are indistinguishable to the caller.
/// </summary>
public sealed class WidgetSettingsLookup(HermesDbContext db)
{
    /// <summary>Active settings or null when absent/disabled. Cheap: single indexed read.</summary>
    public async Task<Domain.Entities.TenantWidgetSettings?> GetActiveAsync(Guid tenantId, CancellationToken ct)
    {
        return await db.TenantWidgetSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.IsActive, ct);
    }

    /// <summary>
    /// Key-exchange lookup: resolves the ACTIVE settings row whose stored key hash
    /// matches the presented raw key. Returns null when no active row matches
    /// (unknown key, disabled widget, or revoked-by-rotation) — indistinguishable
    /// to the caller, fail closed.
    /// </summary>
    public async Task<Domain.Entities.TenantWidgetSettings?> FindActiveByKeyHashAsync(string keyHash, CancellationToken ct)
    {
        return await db.TenantWidgetSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.WidgetApiKeyHash == keyHash && s.IsActive, ct);
    }

    /// <summary>
    /// Checks a presented raw widget key against the stored SHA-256 hex digest.
    /// Constant-time comparison over the equal-length hex digests; a rotation
    /// writes a new digest, so old keys stop matching immediately.
    /// </summary>
    public bool KeyMatches(Domain.Entities.TenantWidgetSettings settings, string presentedKey)
    {
        if (string.IsNullOrEmpty(settings.WidgetApiKeyHash)) return false;
        var presentedHash = WidgetSecrets.Hash(presentedKey);
        var storedHash = settings.WidgetApiKeyHash;
        if (presentedHash.Length != storedHash.Length) return false;
        var mismatch = 0;
        for (var i = 0; i < presentedHash.Length; i++) mismatch |= presentedHash[i] ^ storedHash[i];
        return mismatch == 0;
    }
}
