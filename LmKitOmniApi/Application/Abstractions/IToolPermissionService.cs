namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Service for managing tool permissions per user/role.
/// Addresses OWASP LLM: Tool Misuse & Exploitation, Privilege Abuse.
/// Inspired by console_net/ai-agents/permissions.
/// </summary>
public interface IToolPermissionService
{
    /// <summary>Check if a user can invoke a specific tool.</summary>
    Task<ToolPermissionResult> CanInvokeToolAsync(Guid tenantId, Guid? userId, string userRole, string toolName, CancellationToken ct = default);
    
    /// <summary>Record a tool invocation for audit and rate limiting.</summary>
    Task RecordToolInvocationAsync(Guid tenantId, Guid? userId, string toolName, string? parameters = null, CancellationToken ct = default);
    
    /// <summary>Get all allowed tools for a user role.</summary>
    Task<List<string>> GetAllowedToolsAsync(string userRole, CancellationToken ct = default);
}

public class ToolPermissionResult
{
    public bool IsAllowed { get; set; } = true;
    public string? DenialReason { get; set; }
    public bool RequiresApproval { get; set; }
    
    public static ToolPermissionResult Allow() => new() { IsAllowed = true };
    public static ToolPermissionResult Deny(string reason) => new() { IsAllowed = false, DenialReason = reason };
    public static ToolPermissionResult NeedApproval() => new() { IsAllowed = false, RequiresApproval = true, DenialReason = "Requires human approval" };
}
