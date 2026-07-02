using System.Collections.Concurrent;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Manages tool permissions per role and enforces rate limiting.
/// Addresses OWASP LLM: Tool Misuse, Privilege Escalation.
/// Inspired by console_net/ai-agents/permissions.
/// </summary>
public class ToolPermissionService : IToolPermissionService
{
    private readonly ILogger<ToolPermissionService> _logger;
    
    // Tool invocation tracking for rate limiting (in-memory for simplicity)
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

    public ToolPermissionService(ILogger<ToolPermissionService> logger)
    {
        _logger = logger;
    }

    public Task<ToolPermissionResult> CanInvokeToolAsync(Guid tenantId, Guid? userId, string userRole, string toolName, CancellationToken ct = default)
    {
        // Check 1: Role-based permission
        if (!IsToolAllowedForRole(userRole, toolName))
        {
            _logger.LogWarning("🚫 Tool '{Tool}' denied for role '{Role}' (Tenant: {Tenant}, User: {User})",
                toolName, userRole, tenantId, userId);
            return Task.FromResult(ToolPermissionResult.Deny($"Tool '{toolName}' is not available for role '{userRole}'"));
        }

        // Check 2: Approval required?
        if (ApprovalRequiredTools.Contains(toolName))
        {
            _logger.LogInformation("⚠️ Tool '{Tool}' requires human approval (User: {User})", toolName, userId);
            return Task.FromResult(ToolPermissionResult.NeedApproval());
        }

        // Check 2b: Dynamic MCP Tool names
        if (toolName.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase))
        {
            var mcpToolName = toolName.Substring(4).ToLowerInvariant();
            if (mcpToolName.Contains("write") || mcpToolName.Contains("delete") || 
                mcpToolName.Contains("create") || mcpToolName.Contains("update") || 
                mcpToolName.Contains("execute"))
            {
                _logger.LogInformation("⚠️ MCP Tool '{Tool}' requires human approval (User: {User})", toolName, userId);
                return Task.FromResult(ToolPermissionResult.NeedApproval());
            }
        }

        // Check 3: Rate limiting
        var rateLimitKey = $"{tenantId}:{userId ?? Guid.Empty}:{toolName}";
        if (IsRateLimited(rateLimitKey, toolName))
        {
            _logger.LogWarning("⏱️ Rate limit exceeded for tool '{Tool}' (User: {User})", toolName, userId);
            return Task.FromResult(ToolPermissionResult.Deny($"Rate limit exceeded for '{toolName}'. Please wait before retrying."));
        }

        return Task.FromResult(ToolPermissionResult.Allow());
    }

    public Task RecordToolInvocationAsync(Guid tenantId, Guid? userId, string toolName, string? parameters = null, CancellationToken ct = default)
    {
        var rateLimitKey = $"{tenantId}:{userId ?? Guid.Empty}:{toolName}";
        
        _invocationTracker.AddOrUpdate(
            rateLimitKey,
            _ => new List<DateTime> { DateTime.UtcNow },
            (_, list) =>
            {
                // Clean old entries and add new
                var cutoff = DateTime.UtcNow.AddMinutes(-RateLimitWindowMinutes);
                list.RemoveAll(t => t < cutoff);
                list.Add(DateTime.UtcNow);
                return list;
            });

        _logger.LogInformation("📋 Tool invocation recorded: {Tool} by User {User} (Tenant: {Tenant})",
            toolName, userId, tenantId);

        return Task.CompletedTask;
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
            return allowedTools.Contains(toolName);
        }
        return false; // Unknown role = deny all
    }

    private bool IsRateLimited(string key, string toolName)
    {
        if (!_invocationTracker.TryGetValue(key, out var invocations))
            return false;

        var cutoff = DateTime.UtcNow.AddMinutes(-RateLimitWindowMinutes);
        var recentCount = invocations.Count(t => t >= cutoff);
        
        var limit = ToolRateLimits.TryGetValue(toolName, out var specificLimit)
            ? specificLimit
            : DefaultRateLimit;

        return recentCount >= limit;
    }
}
