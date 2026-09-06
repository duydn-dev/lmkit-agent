using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Minute/day quota for the public widget, keyed by tenant + embedding origin.
/// Counters live in IDistributedCache (Redis in production, in-memory in tests
/// and single-node deployments) with TTLs slightly longer than the window so a
/// bucket cannot be reused. Fail-OPEN on cache outage (widget chat degrades to
/// best-effort rather than hard-failing when Redis blips) — the process-local
/// rate limiter still bounds each replica, and the outage is logged.
/// </summary>
public sealed class WidgetQuotaService(IDistributedCache cache, ILogger<WidgetQuotaService> logger)
{
    public const long DefaultRequestsPerMinute = 60;
    public const long DefaultRequestsPerDay = 10_000;

    /// <summary>Returns true when the request is within quota (and consumes one slot).</summary>
    public async Task<bool> TryConsumeAsync(Guid tenantId, string origin, long perMinute, long perDay, CancellationToken ct)
    {
        if (perMinute <= 0) perMinute = DefaultRequestsPerMinute;
        if (perDay <= 0) perDay = DefaultRequestsPerDay;

        var nowUtc = DateTime.UtcNow;
        var minuteKey = $"widget:q:{tenantId:N}:{origin}:{nowUtc:yyyyMMddHHmm}";
        var dayKey = $"widget:q:{tenantId:N}:{origin}:{nowUtc:yyyyMMdd}";
        try
        {
            var minuteCount = await IncrementWithTtlAsync(minuteKey, TimeSpan.FromMinutes(2), ct);
            var dayCount = await IncrementWithTtlAsync(dayKey, TimeSpan.FromHours(25), ct);
            return minuteCount <= perMinute && dayCount <= perDay;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Widget quota cache unavailable; allowing request best-effort.");
            return true;
        }
    }

    private async Task<long> IncrementWithTtlAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        var valueBytes = await cache.GetAsync(key, ct);
        long next;
        if (valueBytes is { Length: > 0 } && long.TryParse(Encoding.UTF8.GetString(valueBytes), out var current))
            next = current + 1;
        else
            next = 1;
        await cache.SetStringAsync(key, next.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
        return next;
    }
}
