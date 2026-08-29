using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace LmKitOmniApi.Infrastructure.AI.Resilience;

/// <summary>
/// Retry, timeout and circuit-breaker policy for agent tools. Redis deployments use
/// Lua scripts so failure counting and half-open admission are atomic across replicas;
/// the in-memory development fallback is serialized per circuit key.
/// </summary>
public sealed class AgentResiliencePolicy
{
    private const int MaxAttempts = 3;
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CircuitBreakerSamplingWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CircuitStateTtl = CircuitBreakerDuration + CircuitBreakerSamplingWindow;
    private static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim[] LocalStateLocks =
        Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly ConcurrentDictionary<string, CircuitBreakerState> LocalStates = new();

    private readonly ILogger<AgentResiliencePolicy> _logger;
    private readonly IDatabase? _redis;

    public AgentResiliencePolicy(
        ILogger<AgentResiliencePolicy> logger,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis?.GetDatabase();
    }

    public async Task<T> ExecuteWithResilienceAsync<T>(
        string toolName,
        Func<CancellationToken, Task<T>> action,
        T fallbackValue,
        CancellationToken ct = default,
        bool retrySafe = true,
        string? isolationKey = null)
    {
        var circuitKey = BuildCircuitKey(isolationKey ?? toolName);
        var permit = await TryAcquireCircuitPermitAsync(circuitKey, ct);
        if (permit == CircuitPermit.Open)
        {
            _logger.LogWarning("Circuit breaker OPEN for tool '{Tool}'. Using fallback.", toolName);
            return fallbackValue;
        }

        Exception? lastException = null;
        var maximumAttempts = retrySafe ? MaxAttempts : 1;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DefaultToolTimeout);
                var result = await action(timeout.Token);
                await RecordSuccessAsync(circuitKey, permit);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"Tool '{toolName}' timed out after {DefaultToolTimeout.TotalSeconds}s.");
                _logger.LogWarning("Tool '{Tool}' timed out on attempt {Attempt}/{Max}.",
                    toolName, attempt, maximumAttempts);
                if (await RecordFailureAsync(circuitKey, permit)) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning("Tool '{Tool}' failed on attempt {Attempt}/{Max}: {Error}",
                    toolName, attempt, maximumAttempts, ex.Message);
                if (await RecordFailureAsync(circuitKey, permit)) break;
            }

            if (attempt < maximumAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct);
            }
        }

        _logger.LogError("Tool '{Tool}' failed. Using fallback. Last error: {Error}",
            toolName, lastException?.Message);
        return fallbackValue;
    }

    public Task<bool> ExecuteWithResilienceAsync(
        string toolName,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default,
        string? isolationKey = null) =>
        ExecuteWithResilienceAsync(
            toolName,
            async token =>
            {
                await action(token);
                return true;
            },
            false,
            ct,
            isolationKey: isolationKey);

    /// <summary>Executes a required operation and never converts failure into success.</summary>
    public async Task<T> ExecuteRequiredWithResilienceAsync<T>(
        string toolName,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default,
        bool retrySafe = true,
        string? isolationKey = null)
    {
        var circuitKey = BuildCircuitKey(isolationKey ?? toolName);
        var permit = await TryAcquireCircuitPermitAsync(circuitKey, ct);
        if (permit == CircuitPermit.Open)
            throw new InvalidOperationException($"Circuit breaker is open for tool '{toolName}'.");

        Exception? lastException = null;
        var maximumAttempts = retrySafe ? MaxAttempts : 1;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DefaultToolTimeout);
                var result = await action(timeout.Token);
                await RecordSuccessAsync(circuitKey, permit);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"Tool '{toolName}' timed out after {DefaultToolTimeout.TotalSeconds}s.");
                if (await RecordFailureAsync(circuitKey, permit)) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (await RecordFailureAsync(circuitKey, permit)) break;
            }

            if (attempt < maximumAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException(
            $"Tool '{toolName}' failed after at most {maximumAttempts} attempts.", lastException);
    }

    internal static string BuildCircuitKey(string isolationKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(isolationKey));
        return $"LmKitOmniApi_cb:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private async Task<CircuitPermit> TryAcquireCircuitPermitAsync(string cacheKey, CancellationToken ct)
    {
        if (_redis is not null)
        {
            try
            {
                const string script = """
                if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
                local isOpen = tonumber(redis.call('HGET', KEYS[1], 'isOpen') or '0')
                if tonumber(redis.call('HGET', KEYS[1], 'probe') or '0') == 1 then return 1 end
                if isOpen == 0 then return 0 end
                local openedAt = tonumber(redis.call('HGET', KEYS[1], 'openedAt') or '0')
                if tonumber(ARGV[1]) - openedAt <= tonumber(ARGV[2]) then return 1 end
                redis.call('HSET', KEYS[1], 'isOpen', 0, 'probe', 1)
                redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[3]))
                return 2
                """;
                var result = await _redis.ScriptEvaluateAsync(
                    script,
                    [cacheKey],
                    [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        (long)CircuitBreakerDuration.TotalMilliseconds,
                        (long)CircuitStateTtl.TotalMilliseconds]);
                return (CircuitPermit)(int)result;
            }
            catch (Exception ex) when (IsRedisAvailabilityFailure(ex))
            {
                _logger.LogError(ex, "Redis circuit state unavailable; using process-local fallback for {CircuitKey}.", cacheKey);
            }
        }

        var stateLock = GetLocalStateLock(cacheKey);
        await stateLock.WaitAsync(ct);
        try
        {
            if (!LocalStates.TryGetValue(cacheKey, out var state)) return CircuitPermit.Closed;
            var lastStateChange = state.LastFailure ?? state.OpenedAt;
            if (lastStateChange != default && DateTime.UtcNow - lastStateChange > CircuitStateTtl)
            {
                LocalStates.TryRemove(cacheKey, out _);
                return CircuitPermit.Closed;
            }
            if (state.HalfOpenProbeActive) return CircuitPermit.Open;
            if (!state.IsOpen) return CircuitPermit.Closed;
            if (DateTime.UtcNow - state.OpenedAt <= CircuitBreakerDuration)
                return CircuitPermit.Open;

            state.IsOpen = false;
            state.HalfOpenProbeActive = true;
            LocalStates[cacheKey] = state;
            return CircuitPermit.HalfOpen;
        }
        finally
        {
            stateLock.Release();
        }
    }

    /// <returns>True when the circuit is now open and the current retry loop should stop.</returns>
    private async Task<bool> RecordFailureAsync(string cacheKey, CircuitPermit permit)
    {
        if (_redis is not null)
        {
            try
            {
                const string script = """
                local now = tonumber(ARGV[1])
                local threshold = tonumber(ARGV[2])
                local window = tonumber(ARGV[3])
                local ttl = tonumber(ARGV[4])
                local halfOpen = tonumber(ARGV[5])
                if halfOpen == 1 or tonumber(redis.call('HGET', KEYS[1], 'probe') or '0') == 1 then
                  redis.call('HSET', KEYS[1], 'failureCount', threshold, 'firstFailure', now,
                    'lastFailure', now, 'isOpen', 1, 'openedAt', now, 'probe', 0)
                  redis.call('PEXPIRE', KEYS[1], ttl)
                  return 1
                end
                local first = tonumber(redis.call('HGET', KEYS[1], 'firstFailure') or '0')
                local count
                if first == 0 or now - first > window then
                  count = 1
                  redis.call('HSET', KEYS[1], 'firstFailure', now, 'failureCount', count)
                else
                  count = redis.call('HINCRBY', KEYS[1], 'failureCount', 1)
                end
                local opened = 0
                if count >= threshold then
                  redis.call('HSET', KEYS[1], 'isOpen', 1, 'openedAt', now, 'probe', 0)
                  opened = 1
                end
                redis.call('HSET', KEYS[1], 'lastFailure', now)
                redis.call('PEXPIRE', KEYS[1], ttl)
                return opened
                """;
                var result = await _redis.ScriptEvaluateAsync(
                    script,
                    [cacheKey],
                    [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CircuitBreakerThreshold,
                        (long)CircuitBreakerSamplingWindow.TotalMilliseconds,
                        (long)CircuitStateTtl.TotalMilliseconds,
                        permit == CircuitPermit.HalfOpen ? 1 : 0]);
                return (int)result == 1;
            }
            catch (Exception ex) when (IsRedisAvailabilityFailure(ex))
            {
                _logger.LogError(ex, "Redis circuit state unavailable; recording failure in process-local fallback for {CircuitKey}.", cacheKey);
            }
        }

        var stateLock = GetLocalStateLock(cacheKey);
        await stateLock.WaitAsync();
        try
        {
            var state = LocalStates.GetOrAdd(cacheKey, static _ => new CircuitBreakerState());
            var now = DateTime.UtcNow;

            if (permit == CircuitPermit.HalfOpen || state.HalfOpenProbeActive)
            {
                state.FailureCount = CircuitBreakerThreshold;
                state.FirstFailure = now;
                state.LastFailure = now;
                state.IsOpen = true;
                state.OpenedAt = now;
                state.HalfOpenProbeActive = false;
            }
            else
            {
                if (state.FirstFailure.HasValue && now - state.FirstFailure > CircuitBreakerSamplingWindow)
                {
                    state.FailureCount = 0;
                    state.FirstFailure = now;
                }
                state.FirstFailure ??= now;
                state.FailureCount++;
                state.LastFailure = now;
                if (state.FailureCount >= CircuitBreakerThreshold)
                {
                    state.IsOpen = true;
                    state.OpenedAt = now;
                }
            }

            LocalStates[cacheKey] = state;
            return state.IsOpen;
        }
        finally
        {
            stateLock.Release();
        }
    }

    private async Task RecordSuccessAsync(string cacheKey, CircuitPermit permit)
    {
        if (_redis is not null)
        {
            try
            {
                const string script = """
                if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
                local isOpen = tonumber(redis.call('HGET', KEYS[1], 'isOpen') or '0')
                local probe = tonumber(redis.call('HGET', KEYS[1], 'probe') or '0')
                if tonumber(ARGV[1]) == 2 then
                  if probe == 1 then return redis.call('DEL', KEYS[1]) end
                  return 0
                end
                if isOpen == 0 and probe == 0 then return redis.call('DEL', KEYS[1]) end
                return 0
                """;
                await _redis.ScriptEvaluateAsync(script, [cacheKey], [(int)permit]);
                return;
            }
            catch (Exception ex) when (IsRedisAvailabilityFailure(ex))
            {
                _logger.LogError(ex, "Redis circuit state unavailable; clearing process-local fallback for {CircuitKey}.", cacheKey);
            }
        }

        var stateLock = GetLocalStateLock(cacheKey);
        await stateLock.WaitAsync();
        try
        {
            if (!LocalStates.TryGetValue(cacheKey, out var state)) return;
            var canReset = permit == CircuitPermit.HalfOpen
                ? state?.HalfOpenProbeActive == true
                : state is { IsOpen: false, HalfOpenProbeActive: false };
            if (canReset) LocalStates.TryRemove(cacheKey, out _);
        }
        finally
        {
            stateLock.Release();
        }
    }

    private enum CircuitPermit
    {
        Closed = 0,
        Open = 1,
        HalfOpen = 2
    }

    private static bool IsRedisAvailabilityFailure(Exception exception) =>
        exception is RedisConnectionException or RedisTimeoutException or ObjectDisposedException;

    private static SemaphoreSlim GetLocalStateLock(string cacheKey) =>
        LocalStateLocks[(int)((uint)cacheKey.GetHashCode() % LocalStateLocks.Length)];

    private sealed class CircuitBreakerState
    {
        public int FailureCount { get; set; }
        public DateTime? FirstFailure { get; set; }
        public DateTime? LastFailure { get; set; }
        public bool IsOpen { get; set; }
        public DateTime OpenedAt { get; set; }
        public bool HalfOpenProbeActive { get; set; }
    }
}
