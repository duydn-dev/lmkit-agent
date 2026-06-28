using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Observability;

/// <summary>
/// Agent telemetry service for tracing and metrics.
/// Provides structured tracking of agent execution steps using ActivitySource (OpenTelemetry compatible).
/// Inspired by console_net/ai-agents/observability.
/// </summary>
public class AgentTelemetryService
{
    private static readonly ActivitySource ActivitySource = new("LmKitOmniApi.Agent", "1.0.0");
    private readonly ILogger<AgentTelemetryService> _logger;

    // In-memory metrics (for simple dashboard; production would use Prometheus/OTLP)
    private static long _totalRequests;
    private static long _totalTokensEstimated;
    private static long _totalToolInvocations;
    private static long _totalErrors;
    private static readonly Dictionary<string, long> _toolUsageCount = new();
    private static readonly object _metricsLock = new();

    public AgentTelemetryService(ILogger<AgentTelemetryService> logger)
    {
        _logger = logger;
    }

    /// <summary>Start a traced agent execution span.</summary>
    public Activity? StartAgentExecution(string operationName, Guid tenantId, string query)
    {
        Interlocked.Increment(ref _totalRequests);

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
        Interlocked.Increment(ref _totalToolInvocations);
        
        lock (_metricsLock)
        {
            _toolUsageCount.TryGetValue(toolName, out var count);
            _toolUsageCount[toolName] = count + 1;
        }

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
        Interlocked.Add(ref _totalTokensEstimated, estimatedTokens);
    }

    /// <summary>Record an error.</summary>
    public void RecordError(Activity? activity, Exception ex)
    {
        Interlocked.Increment(ref _totalErrors);
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
        Dictionary<string, long> toolUsage;
        lock (_metricsLock)
        {
            toolUsage = new Dictionary<string, long>(_toolUsageCount);
        }

        return new AgentMetricsSnapshot
        {
            TotalRequests = Interlocked.Read(ref _totalRequests),
            TotalTokensEstimated = Interlocked.Read(ref _totalTokensEstimated),
            TotalToolInvocations = Interlocked.Read(ref _totalToolInvocations),
            TotalErrors = Interlocked.Read(ref _totalErrors),
            ToolUsageBreakdown = toolUsage
        };
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
