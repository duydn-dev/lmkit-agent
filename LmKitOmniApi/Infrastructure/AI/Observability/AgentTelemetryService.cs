using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Observability;

/// <summary>
/// Agent telemetry service for tracing and metrics.
/// Provides structured tracking of agent execution steps using ActivitySource (OpenTelemetry compatible).
/// </summary>
public class AgentTelemetryService
{
    private static readonly ActivitySource ActivitySource = new("LmKitOmniApi.Agent", "1.0.0");
    private static readonly Meter AgentMeter = new("LmKitOmniApi.AgentMetrics", "1.0.0");

    private readonly Counter<long> _requestsCounter;
    private readonly Counter<long> _tokensCounter;
    private readonly Counter<long> _toolsCounter;
    private readonly Counter<long> _errorsCounter;

    private readonly ILogger<AgentTelemetryService> _logger;

    public AgentTelemetryService(ILogger<AgentTelemetryService> logger)
    {
        _logger = logger;
        
        _requestsCounter = AgentMeter.CreateCounter<long>("agent_requests_total", description: "Total number of agent requests");
        _tokensCounter = AgentMeter.CreateCounter<long>("agent_tokens_estimated_total", description: "Total estimated tokens used");
        _toolsCounter = AgentMeter.CreateCounter<long>("agent_tools_invocations_total", description: "Total number of tool invocations");
        _errorsCounter = AgentMeter.CreateCounter<long>("agent_errors_total", description: "Total number of errors encountered");
    }

    /// <summary>Start a traced agent execution span.</summary>
    public Activity? StartAgentExecution(string operationName, Guid tenantId, string query)
    {
        _requestsCounter.Add(1, new KeyValuePair<string, object?>("tenant_id", tenantId.ToString()), new KeyValuePair<string, object?>("operation", operationName));

        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag("agent.tenant_id", tenantId.ToString());
        activity?.SetTag("agent.query_length", query.Length);
        activity?.SetTag("agent.query_preview", query.Length > 100 ? query.Substring(0, 100) : query);

        _logger.LogInformation("📊 [Telemetry] Started: {Operation} (Tenant: {Tenant})", operationName, tenantId);
        return activity;
    }

    /// <summary>Start a traced tool invocation span.</summary>
    public Activity? StartToolInvocation(string toolName, Activity? parentActivity = null)
    {
        _toolsCounter.Add(1, new KeyValuePair<string, object?>("tool_name", toolName));

        var activity = ActivitySource.StartActivity($"Tool.{toolName}", ActivityKind.Internal, parentActivity?.Context ?? default);
        activity?.SetTag("tool.name", toolName);

        return activity;
    }

    /// <summary>Record a ReAct iteration.</summary>
    public void RecordReActIteration(Activity? parentActivity, int iteration, string action, string observationPreview)
    {
        using var activity = ActivitySource.StartActivity($"ReAct.Iteration.{iteration}", ActivityKind.Internal, parentActivity?.Context ?? default);
        activity?.SetTag("react.iteration", iteration);
        activity?.SetTag("react.action", action);
        activity?.SetTag("react.observation_length", observationPreview.Length);

        _logger.LogInformation("📊 [Telemetry] ReAct #{Iter}: Action={Action}", iteration, action);
    }

    /// <summary>Record estimated token usage.</summary>
    public void RecordTokenUsage(int estimatedTokens)
    {
        _tokensCounter.Add(estimatedTokens);
    }

    /// <summary>Record an error.</summary>
    public void RecordError(Activity? activity, Exception ex)
    {
        _errorsCounter.Add(1, new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));

        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.AddEvent(new ActivityEvent("error", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().Name },
            { "exception.message", ex.Message }
        }));
    }

    /// <summary>Get current metrics snapshot (for dashboard/API).</summary>
    public AgentMetricsSnapshot GetMetrics()
    {
        // Snapshot is now obsolete since we use OpenTelemetry.
        // Returning empty snapshot to prevent compilation errors if it's used elsewhere.
        return new AgentMetricsSnapshot();
    }
}

public class AgentMetricsSnapshot
{
    public long TotalRequests { get; set; }
    public long TotalTokensEstimated { get; set; }
    public long TotalToolInvocations { get; set; }
    public long TotalErrors { get; set; }
    public Dictionary<string, long> ToolUsageBreakdown { get; set; } = new();
}
