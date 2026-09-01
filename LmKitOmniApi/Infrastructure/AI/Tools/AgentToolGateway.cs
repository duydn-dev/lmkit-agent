using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Observability;
using LmKitOmniApi.Infrastructure.AI.Resilience;
using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// The single execution boundary used by specialized agents.
/// It prevents a delegated agent from bypassing role policy, rate limits,
/// timeout/output budgets, resilience and invocation auditing.
/// </summary>
public sealed class AgentToolGateway
{
    private readonly IToolPermissionService _permissionService;
    private readonly ToolSandboxService _sandbox;
    private readonly AgentResiliencePolicy _resilience;
    private readonly AgentTelemetryService _telemetry;
    private readonly AgentToolAuditService _audit;
    private readonly ILogger<AgentToolGateway> _logger;

    public AgentToolGateway(
        IToolPermissionService permissionService,
        ToolSandboxService sandbox,
        AgentResiliencePolicy resilience,
        AgentTelemetryService telemetry,
        AgentToolAuditService audit,
        ILogger<AgentToolGateway> logger)
    {
        _permissionService = permissionService;
        _sandbox = sandbox;
        _resilience = resilience;
        _telemetry = telemetry;
        _audit = audit;
        _logger = logger;
    }

    public async Task<AgentToolGatewayResult> ExecuteReadOnlyAsync(
        Guid tenantId,
        Guid? userId,
        string userRole,
        string toolName,
        string? auditParameters,
        Func<CancellationToken, Task<string>> action,
        CancellationToken ct = default)
    {
        var toolCallId = Guid.NewGuid();
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var permission = await _permissionService.CanInvokeToolAsync(
            tenantId, userId, userRole, toolName, ct);

        if (!permission.IsAllowed)
        {
            var reason = permission.RequiresApproval
                ? $"Tool '{toolName}' requires explicit approval."
                : permission.DenialReason ?? $"Tool '{toolName}' is not allowed.";

            _logger.LogWarning("Delegated tool {Tool} denied for user {User}: {Reason}",
                toolName, userId, reason);
            await _audit.RecordAsync(
                tenantId, userId, toolCallId, toolName, auditParameters,
                permission.RequiresApproval ? "approval_required" : "denied",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt),
                ct: ct);
            return AgentToolGatewayResult.Denied(reason, permission.RequiresApproval);
        }

        using var activity = _telemetry.StartToolInvocation(toolName);
        var status = "failed";

        try
        {
            var result = await _resilience.ExecuteWithResilienceAsync(
                toolName,
                async resilienceToken =>
                {
                    var sandboxResult = await _sandbox.ExecuteInSandboxAsync(
                        toolName,
                        action,
                        resilienceToken);

                    if (sandboxResult.IsSuccess)
                    {
                        return AgentToolGatewayResult.Success(sandboxResult.Output);
                    }

                    if (sandboxResult.IsBlocked)
                    {
                        return AgentToolGatewayResult.Denied(
                            sandboxResult.ErrorMessage ?? $"Tool '{toolName}' was blocked by the sandbox.",
                            requiresApproval: false);
                    }

                    throw new InvalidOperationException(
                        sandboxResult.ErrorMessage ?? $"Tool '{toolName}' failed.");
                },
                AgentToolGatewayResult.Failed($"Tool '{toolName}' is temporarily unavailable."),
                ct,
                isolationKey: $"{tenantId:N}:{toolName}");

            if (result.IsSuccess)
            {
                status = "succeeded";
                await _permissionService.RecordToolInvocationAsync(
                    tenantId, userId, toolName, auditParameters, ct);
            }

            else if (result.IsDenied) status = "denied";
            return result;
        }
        finally
        {
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            _telemetry.RecordToolDuration(toolName, duration);
            await _audit.RecordAsync(
                tenantId, userId, toolCallId, toolName, auditParameters,
                status, duration, ct: CancellationToken.None);
        }
    }
}

public sealed record AgentToolGatewayResult(
    bool IsSuccess,
    bool IsDenied,
    bool RequiresApproval,
    string Output,
    string? ErrorMessage)
{
    public static AgentToolGatewayResult Success(string output) =>
        new(true, false, false, output, null);

    public static AgentToolGatewayResult Failed(string error) =>
        new(false, false, false, string.Empty, error);

    public static AgentToolGatewayResult Denied(string error, bool requiresApproval) =>
        new(false, true, requiresApproval, string.Empty, error);
}
