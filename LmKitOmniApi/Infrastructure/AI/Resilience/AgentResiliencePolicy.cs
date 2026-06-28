using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Resilience;

/// <summary>
/// Resilience policies for agent tool execution.
/// Provides retry, circuit breaker, and timeout patterns.
/// Inspired by console_net/ai-agents/resilience.
/// </summary>
public class AgentResiliencePolicy
{
    private readonly ILogger<AgentResiliencePolicy> _logger;

    // Retry: 3 attempts with exponential backoff
    private const int MaxRetries = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);

    // Circuit Breaker: open after 5 failures in 60s, stay open for 30s
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CircuitBreakerSamplingWindow = TimeSpan.FromSeconds(60);

    // Tool timeout
    private static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(30);

    // Per-tool circuit breaker state (simple in-memory implementation)
    private readonly Dictionary<string, CircuitBreakerState> _circuitStates = new();
    private readonly object _stateLock = new();

    public AgentResiliencePolicy(ILogger<AgentResiliencePolicy> logger)
    {
        _logger = logger;
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
        if (IsCircuitOpen(toolName))
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
                RecordSuccess(toolName);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("⏱️ Tool '{Tool}' timed out on attempt {Attempt}/{Max}", toolName, attempt, MaxRetries);
                lastException = new TimeoutException($"Tool '{toolName}' timed out after {DefaultToolTimeout.TotalSeconds}s");
                RecordFailure(toolName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("🔄 Tool '{Tool}' failed on attempt {Attempt}/{Max}: {Error}",
                    toolName, attempt, MaxRetries, ex.Message);
                lastException = ex;
                RecordFailure(toolName);
            }

            if (attempt < MaxRetries)
            {
                // Exponential backoff (L3 Fix: explicit ms calculation for clarity)
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

    private bool IsCircuitOpen(string toolName)
    {
        lock (_stateLock)
        {
            if (!_circuitStates.TryGetValue(toolName, out var state))
                return false;

            if (state.IsOpen && DateTime.UtcNow - state.OpenedAt > CircuitBreakerDuration)
            {
                // Half-open: allow one request through
                state.IsOpen = false;
                state.FailureCount = 0;
                _logger.LogInformation("⚡ Circuit breaker HALF-OPEN for '{Tool}'. Allowing test request.", toolName);
                return false;
            }

            return state.IsOpen;
        }
    }

    private void RecordFailure(string toolName)
    {
        lock (_stateLock)
        {
            if (!_circuitStates.TryGetValue(toolName, out var state))
            {
                state = new CircuitBreakerState();
                _circuitStates[toolName] = state;
            }

            state.FailureCount++;
            state.LastFailure = DateTime.UtcNow;

            // Clean old failures outside sampling window
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
        }
    }

    private void RecordSuccess(string toolName)
    {
        lock (_stateLock)
        {
            if (_circuitStates.TryGetValue(toolName, out var state))
            {
                state.FailureCount = 0;
                state.IsOpen = false;
                state.FirstFailure = null;
            }
        }
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
