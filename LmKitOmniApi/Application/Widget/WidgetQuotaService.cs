using System.Collections.Concurrent;
using StackExchange.Redis;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Minute/day quota for the public widget, keyed by tenant + embedding origin.
/// <para>
/// Counting is ATOMIC on both paths. When Redis is configured the counter is a
/// single <c>INCR</c> + first-write <c>EXPIRE</c> Lua script (the same pattern as
/// <c>DistributedAiRateLimitMiddleware</c>), so concurrent widget turns across
/// replicas cannot interleave a read-modify-write and blow past the budget.
/// Without Redis (single node, tests) the counter is a process-local
/// <see cref="Interlocked"/> increment over the same key/TTL scheme — still a real
/// bound, just per replica.
/// </para>
/// <para>
/// A Redis outage does NOT fail open: the request falls back to the process-local
/// counter (logged as degraded), so a cache blip cannot hand out unlimited
/// anonymous inference.
/// </para>
/// </summary>
public sealed class WidgetQuotaService(ILogger<WidgetQuotaService> logger, IConnectionMultiplexer? redis = null)
{
    public const long DefaultRequestsPerMinute = 60;
    public const long DefaultRequestsPerDay = 10_000;

    /// <summary>
    /// Atomic increment with a TTL applied only on creation. Mirrors
    /// <c>DistributedAiRateLimitMiddleware.IncrementScript</c>.
    /// </summary>
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    /// <summary>Windows are keyed by minute/day stamp, so a key is never reused; entries are pruned by TTL.</summary>
    private static readonly ConcurrentDictionary<string, LocalWindow> LocalWindows = new(StringComparer.Ordinal);

    private const int LocalWindowPruneThreshold = 2048;

    /// <summary>Returns true when the request is within quota (and consumes one slot).</summary>
    public async Task<bool> TryConsumeAsync(Guid tenantId, string origin, long perMinute, long perDay, CancellationToken ct)
    {
        if (perMinute <= 0) perMinute = DefaultRequestsPerMinute;
        if (perDay <= 0) perDay = DefaultRequestsPerDay;

        ct.ThrowIfCancellationRequested();

        var nowUtc = DateTime.UtcNow;
        var minuteKey = $"widget:q:{tenantId:N}:{origin}:{nowUtc:yyyyMMddHHmm}";
        var dayKey = $"widget:q:{tenantId:N}:{origin}:{nowUtc:yyyyMMdd}";

        // Both counters are consumed even when the first one is already over budget:
        // a rejected turn still counted against the caller, which is what stops a
        // hot loop from resetting its own day budget by racing the minute window.
        //
        // These are two SEPARATE atomic operations, not one. Each counter admits exactly its
        // budget, but under contention not necessarily the same callers — another caller's day
        // increment can land between this caller's two — so grants are the intersection and can
        // come in one or two under budget. That is the fail-closed direction (a caller sees a
        // spurious rejection; nobody gets extra) and it is why
        // WidgetQuotaServiceTests.TryConsume_UnderConcurrency_NeverExceedsTheBudget asserts a
        // cap rather than equality. Making it exact means one atomic operation spanning both
        // counters, on both the Redis and the local path.
        var minuteCount = await IncrementAsync(minuteKey, TimeSpan.FromMinutes(2), nowUtc);
        var dayCount = await IncrementAsync(dayKey, TimeSpan.FromHours(25), nowUtc);

        return minuteCount <= perMinute && dayCount <= perDay;
    }

    private async Task<long> IncrementAsync(string key, TimeSpan ttl, DateTime nowUtc)
    {
        if (redis is not null)
        {
            try
            {
                var result = await redis.GetDatabase().ScriptEvaluateAsync(
                    IncrementScript,
                    new RedisKey[] { key },
                    new RedisValue[] { (long)ttl.TotalSeconds });
                if (!result.IsNull) return (long)result;
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "Widget quota counter unavailable in Redis; falling back to the process-local counter.");
            }
        }

        return IncrementLocal(key, ttl, nowUtc);
    }

    private static long IncrementLocal(string key, TimeSpan ttl, DateTime nowUtc)
    {
        if (LocalWindows.Count > LocalWindowPruneThreshold) PruneLocal(nowUtc);
        var window = LocalWindows.GetOrAdd(key, _ => new LocalWindow(nowUtc + ttl));
        return Interlocked.Increment(ref window.Count);
    }

    private static void PruneLocal(DateTime nowUtc)
    {
        foreach (var (key, window) in LocalWindows)
        {
            if (window.ExpiresAtUtc <= nowUtc) LocalWindows.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Test hook: drops the process-local counter windows of ONE tenant. Scoped to a
    /// tenant on purpose — the store is process-wide, and a blanket clear would let
    /// one test class reset another's budget mid-assertion.
    /// </summary>
    internal static void ResetLocalCountersForTests(Guid tenantId)
    {
        var prefix = $"widget:q:{tenantId:N}:";
        foreach (var key in LocalWindows.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) LocalWindows.TryRemove(key, out _);
        }
    }

    private sealed class LocalWindow(DateTime expiresAtUtc)
    {
        public long Count;
        public readonly DateTime ExpiresAtUtc = expiresAtUtc;
    }
}
