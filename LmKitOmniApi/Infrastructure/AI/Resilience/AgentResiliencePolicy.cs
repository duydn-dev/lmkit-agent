using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Resilience;

/// <summary>
/// Resilience policies for agent tool execution.
/// Provides retry, circuit breaker, and timeout patterns backed by IDistributedCache.
/// Inspired by console_net/ai-agents/resilience.
/// </summary>
public class AgentResiliencePolicy
{
    private readonly ILogger<AgentResiliencePolicy> _logger;
    private readonly IDistributedCache _cache;

    // Retry: 3 attempts with exponential backoff
    private const int MaxRetries = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);

    // Circuit Breaker: open after 5 failures in 60s, stay open for 30s
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CircuitBreakerSamplingWindow = TimeSpan.FromSeconds(60);

    // Tool timeout
    private static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(30);

    public AgentResiliencePolicy(ILogger<AgentResiliencePolicy> logger, IDistributedCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Execute a tool action with retry + circuit breaker + timeout.
    /// </summary>
    public async Task<T> ExecuteWithResilienceAsync<T>(
        string toolName,
        Func<CancellationToken, Task<T>> action,
        T fallbackValue,
        CancellationToken ct = default)
    {
        // Check circuit breaker
        if (await IsCircuitOpenAsync(toolName, ct))
        {
            _logger.LogWarning("⚡ Circuit breaker OPEN for tool '{Tool}'. Using fallback.", toolName);
            return fallbackValue;
        }

        var lastException = (Exception?)null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Timeout wrapper
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DefaultToolTimeout);

                var result = await action(cts.Token);
                
                // Success — record it
                await RecordSuccessAsync(toolName, ct);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("⏱️ Tool '{Tool}' timed out on attempt {Attempt}/{Max}", toolName, attempt, MaxRetries);
                lastException = new TimeoutException($"Tool '{toolName}' timed out after {DefaultToolTimeout.TotalSeconds}s");
                await RecordFailureAsync(toolName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("🔄 Tool '{Tool}' failed on attempt {Attempt}/{Max}: {Error}",
                    toolName, attempt, MaxRetries, ex.Message);
                lastException = ex;
                await RecordFailureAsync(toolName, ct);
            }

            if (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                _logger.LogInformation("Retrying '{Tool}' in {Delay}ms...", toolName, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }

        _logger.LogError("❌ Tool '{Tool}' failed after {Max} attempts. Using fallback. Last error: {Error}",
            toolName, MaxRetries, lastException?.Message);
        return fallbackValue;
    }

    /// <summary>
    /// Execute a void action with resilience (returns success/failure).
    /// </summary>
    public async Task<bool> ExecuteWithResilienceAsync(
        string toolName,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        var result = await ExecuteWithResilienceAsync(
            toolName,
            async (token) => { await action(token); return true; },
            false,
            ct);
        return result;
    }

    private async Task<bool> IsCircuitOpenAsync(string toolName, CancellationToken ct)
    {
        var cacheKey = $"cb_state:{toolName}";
        var json = await _cache.GetStringAsync(cacheKey, ct);
        if (string.IsNullOrEmpty(json)) return false;

        var state = JsonSerializer.Deserialize<CircuitBreakerState>(json);
        if (state == null) return false;

        if (state.IsOpen && DateTime.UtcNow - state.OpenedAt > CircuitBreakerDuration)
        {
            // Half-open: allow one request through
            state.IsOpen = false;
            state.FailureCount = 0;
            _logger.LogInformation("⚡ Circuit breaker HALF-OPEN for '{Tool}'. Allowing test request.", toolName);
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(state), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CircuitBreakerDuration + CircuitBreakerSamplingWindow
            }, ct);
            return false;
        }

        return state.IsOpen;
    }

    private async Task RecordFailureAsync(string toolName, CancellationToken ct)
    {
        var cacheKey = $"cb_state:{toolName}";
        var json = await _cache.GetStringAsync(cacheKey, ct);
        var state = string.IsNullOrEmpty(json) ? new CircuitBreakerState() : JsonSerializer.Deserialize<CircuitBreakerState>(json) ?? new CircuitBreakerState();

        state.FailureCount++;
        state.LastFailure = DateTime.UtcNow;

        if (state.FirstFailure.HasValue && DateTime.UtcNow - state.FirstFailure > CircuitBreakerSamplingWindow)
        {
            state.FailureCount = 1;
            state.FirstFailure = DateTime.UtcNow;
        }

        state.FirstFailure ??= DateTime.UtcNow;

        if (state.FailureCount >= CircuitBreakerThreshold)
        {
            state.IsOpen = true;
            state.OpenedAt = DateTime.UtcNow;
            _logger.LogWarning("⚡ Circuit breaker OPENED for '{Tool}' after {Count} failures.", toolName, state.FailureCount);
        }

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(state), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CircuitBreakerDuration + CircuitBreakerSamplingWindow
        }, ct);
    }

    private async Task RecordSuccessAsync(string toolName, CancellationToken ct)
    {
        var cacheKey = $"cb_state:{toolName}";
        await _cache.RemoveAsync(cacheKey, ct);
    }

    private class CircuitBreakerState
    {
        public int FailureCount { get; set; }
        public DateTime? FirstFailure { get; set; }
        public DateTime? LastFailure { get; set; }
        public bool IsOpen { get; set; }
        public DateTime OpenedAt { get; set; }
    }
}
