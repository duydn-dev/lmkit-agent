using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Manages tool permissions per role and enforces rate limiting.
/// Addresses OWASP LLM: Tool Misuse, Privilege Escalation.
/// Inspired by console_net/ai-agents/permissions.
/// When Redis is configured the quota window is a Redis sorted set updated by Lua
/// scripts, so counts are atomic across replicas and survive restarts (mirrors
/// <see cref="Resilience.AgentResiliencePolicy"/>); the in-process tracker remains
/// the single-node fallback and the degradation path when Redis is unavailable.
/// </summary>
public class ToolPermissionService : IToolPermissionService
{
    private readonly ILogger<ToolPermissionService> _logger;
    private readonly IDatabase? _redis;

    // Tool invocation tracking for rate limiting (single-node / Redis-outage fallback)
    private readonly ConcurrentDictionary<string, List<DateTime>> _invocationTracker = new();
    
    // Role-based tool whitelist
    private static readonly Dictionary<string, HashSet<string>> RoleToolPermissions = new()
    {
        ["Admin"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SearchWeb", "ReadPdfDocument", "AnalyzeImage", "TranscribeAudio",
            "AnalyzeText", "QueryKnowledgeBase", "IngestDocument",
            "ReadWordDocument", "ReadExcelDocument",
            "Delegate", "MCP" // C3 Fix: added for action→tool mapping
        },
        ["User"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SearchWeb", "ReadPdfDocument", "AnalyzeImage", "TranscribeAudio",
            "AnalyzeText", "QueryKnowledgeBase",
            "Delegate" // C3 Fix: Users can delegate but not use MCP
        },
        ["Guest"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SearchWeb", "AnalyzeText"
        }
    };

    // Tools that require human approval before execution
    private static readonly HashSet<string> ApprovalRequiredTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "IngestDocument", "DeleteDocument"
    };

    // Rate limits: max invocations per minute per user
    private static readonly Dictionary<string, int> ToolRateLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SearchWeb"] = 10,
        ["ReadPdfDocument"] = 20,
        ["AnalyzeImage"] = 10,
        ["TranscribeAudio"] = 5,
        ["IngestDocument"] = 5,
        ["QueryKnowledgeBase"] = 30,
        ["AnalyzeText"] = 20,
        ["ReadWordDocument"] = 15,
        ["ReadExcelDocument"] = 15,
    };

    private const int DefaultRateLimit = 20; // per minute
    private const int RateLimitWindowMinutes = 1;

    public ToolPermissionService(
        ILogger<ToolPermissionService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _redis = redis?.GetDatabase();
    }

    public async Task<ToolPermissionResult> CanInvokeToolAsync(Guid tenantId, Guid? userId, string userRole, string toolName, CancellationToken ct = default)
    {
        // Check 1: Role-based permission
        if (!IsToolAllowedForRole(userRole, toolName))
        {
            _logger.LogWarning("🚫 Tool '{Tool}' denied for role '{Role}' (Tenant: {Tenant}, User: {User})",
                toolName, userRole, tenantId, userId);
            return ToolPermissionResult.Deny($"Tool '{toolName}' is not available for role '{userRole}'");
        }

        // Check 2: Approval required?
        if (ApprovalRequiredTools.Contains(toolName))
        {
            _logger.LogInformation("⚠️ Tool '{Tool}' requires human approval (User: {User})", toolName, userId);
            return ToolPermissionResult.NeedApproval();
        }

        // Check 2b: Dynamic MCP Tool names
        if (toolName.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase))
        {
            // MCP annotations are untrusted hints. TRUSTED_READ is emitted only when a tenant
            // admin explicitly trusts that server's read-only annotations; every other tool
            // fails safe to human approval regardless of a benign-looking name.
            if (!toolName.StartsWith("MCP:TRUSTED_READ:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("⚠️ MCP Tool '{Tool}' requires human approval (User: {User})", toolName, userId);
                return ToolPermissionResult.NeedApproval();
            }
        }

        // Check 3: Rate limiting
        var rateLimitKey = $"{tenantId}:{userId ?? Guid.Empty}:{toolName}";
        if (await IsRateLimitedAsync(rateLimitKey, toolName))
        {
            _logger.LogWarning("⏱️ Rate limit exceeded for tool '{Tool}' (User: {User})", toolName, userId);
            return ToolPermissionResult.Deny($"Rate limit exceeded for '{toolName}'. Please wait before retrying.");
        }

        return ToolPermissionResult.Allow();
    }

    public async Task RecordToolInvocationAsync(Guid tenantId, Guid? userId, string toolName, string? parameters = null, CancellationToken ct = default)
    {
        var rateLimitKey = $"{tenantId}:{userId ?? Guid.Empty}:{toolName}";

        if (!await TryRecordInvocationInRedisAsync(rateLimitKey))
            RecordInvocationLocally(rateLimitKey);

        _logger.LogInformation("📋 Tool invocation recorded: {Tool} by User {User} (Tenant: {Tenant})",
            toolName, userId, tenantId);
    }

    public Task<List<string>> GetAllowedToolsAsync(string userRole, CancellationToken ct = default)
    {
        if (RoleToolPermissions.TryGetValue(userRole, out var tools))
        {
            return Task.FromResult(tools.ToList());
        }

        // Default: no tools allowed for unknown roles
        return Task.FromResult(new List<string>());
    }

    private bool IsToolAllowedForRole(string role, string toolName)
    {
        if (RoleToolPermissions.TryGetValue(role, out var allowedTools))
        {
            return allowedTools.Contains(toolName)
                || (toolName.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase)
                    && allowedTools.Contains("MCP"));
        }
        return false; // Unknown role = deny all
    }

    private async Task<bool> IsRateLimitedAsync(string key, string toolName)
    {
        var limit = ToolRateLimits.TryGetValue(toolName, out var specificLimit)
            ? specificLimit
            : DefaultRateLimit;

        var redisCount = await TryCountRecentInvocationsInRedisAsync(key);
        if (redisCount is not null)
            return redisCount.Value >= limit;

        return CountRecentInvocationsLocally(key) >= limit;
    }

    // ── Redis-backed sliding window (atomic across replicas) ──

    private static readonly TimeSpan QuotaWindow = TimeSpan.FromMinutes(RateLimitWindowMinutes);

    /// <returns>The in-window invocation count, or null when Redis is not configured/available.</returns>
    private async Task<long?> TryCountRecentInvocationsInRedisAsync(string rateLimitKey)
    {
        if (_redis is null) return null;

        try
        {
            const string script = """
            redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, tonumber(ARGV[1]) - tonumber(ARGV[2]))
            return redis.call('ZCARD', KEYS[1])
            """;
            var result = await _redis.ScriptEvaluateAsync(
                script,
                [BuildQuotaKey(rateLimitKey)],
                [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)QuotaWindow.TotalMilliseconds]);
            return (long)result;
        }
        catch (Exception ex) when (IsRedisAvailabilityFailure(ex))
        {
            _logger.LogError(ex, "Redis tool quota unavailable; using process-local invocation counts.");
            return null;
        }
    }

    /// <returns>True when the invocation was recorded in Redis; false to use the local fallback.</returns>
    private async Task<bool> TryRecordInvocationInRedisAsync(string rateLimitKey)
    {
        if (_redis is null) return false;

        try
        {
            const string script = """
            redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, tonumber(ARGV[1]) - tonumber(ARGV[2]))
            redis.call('ZADD', KEYS[1], tonumber(ARGV[1]), ARGV[3])
            redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
            return 1
            """;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _redis.ScriptEvaluateAsync(
                script,
                [BuildQuotaKey(rateLimitKey)],
                [nowMs, (long)QuotaWindow.TotalMilliseconds, $"{nowMs}-{Guid.NewGuid():N}"]);
            return true;
        }
        catch (Exception ex) when (IsRedisAvailabilityFailure(ex))
        {
            _logger.LogError(ex, "Redis tool quota unavailable; recording invocation in process-local fallback.");
            return false;
        }
    }

    // ── In-process fallback (identical to the original single-node behavior) ──

    private void RecordInvocationLocally(string rateLimitKey) =>
        _invocationTracker.AddOrUpdate(
            rateLimitKey,
            _ => new List<DateTime> { DateTime.UtcNow },
            (_, list) =>
            {
                lock (list)
                {
                    var cutoff = DateTime.UtcNow.AddMinutes(-RateLimitWindowMinutes);
                    list.RemoveAll(t => t < cutoff);
                    list.Add(DateTime.UtcNow);
                }
                return list;
            });

    private int CountRecentInvocationsLocally(string key)
    {
        if (!_invocationTracker.TryGetValue(key, out var invocations))
            return 0;

        var cutoff = DateTime.UtcNow.AddMinutes(-RateLimitWindowMinutes);
        lock (invocations)
        {
            invocations.RemoveAll(t => t < cutoff);
            return invocations.Count;
        }
    }

    // Hash the tenant:user:tool key so Redis never stores raw tenant/user ids or tool
    // names (same convention as AgentResiliencePolicy.BuildCircuitKey).
    private static string BuildQuotaKey(string rateLimitKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rateLimitKey));
        return $"LmKitOmniApi_tq:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private static bool IsRedisAvailabilityFailure(Exception exception) =>
        exception is RedisConnectionException or RedisTimeoutException or ObjectDisposedException;
}
