using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Agents;

/// <summary>
/// Multi-Agent Orchestrator — coordinator pattern.
/// Routes queries to the most appropriate specialized agent(s),
/// can run multiple agents in parallel, and merges their results.
/// Inspired by console_net/ai-agents/multi-agent-workflows + delegation.
/// </summary>
public class MultiAgentOrchestrator
{
    private readonly IEnumerable<ISpecializedAgent> _agents;
    private readonly ILogger<MultiAgentOrchestrator> _logger;

    // Confidence threshold for an agent to be selected
    private const double MinConfidenceThreshold = 0.2;
    // Max agents to run in parallel for a single query
    private const int MaxParallelAgents = 3;

    public MultiAgentOrchestrator(IEnumerable<ISpecializedAgent> agents, ILogger<MultiAgentOrchestrator> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    /// <summary>
    /// Route a query to the best matching agent and execute it.
    /// Returns the agent's result as context for the final LLM generation.
    /// </summary>
    public async Task<string> RouteAndExecuteAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct = default)
    {
        // Step 1: Evaluate all agents' confidence for this query
        var evaluations = new List<(ISpecializedAgent Agent, double Confidence)>();

        foreach (var agent in _agents)
        {
            try
            {
                var confidence = await agent.EvaluateConfidenceAsync(query, ct);
                if (confidence >= MinConfidenceThreshold)
                {
                    evaluations.Add((agent, confidence));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Agent '{Agent}' confidence evaluation failed: {Error}", agent.AgentName, ex.Message);
            }
        }

        if (evaluations.Count == 0)
        {
            _logger.LogInformation("🤖 No specialized agent matched for query. Using general chat.");
            return string.Empty;
        }

        // Step 2: Select top agents by confidence
        var selectedAgents = evaluations
            .OrderByDescending(e => e.Confidence)
            .Take(MaxParallelAgents)
            .ToList();

        _logger.LogInformation("🤖 Selected {Count} agent(s): [{Agents}]",
            selectedAgents.Count,
            string.Join(", ", selectedAgents.Select(a => $"{a.Agent.AgentName}({a.Confidence:P0})")));

        // Step 3: Execute agents (parallel if multiple)
        var results = new List<AgentExecutionResult>();

        if (selectedAgents.Count == 1)
        {
            // Single agent — sequential
            var (agent, _) = selectedAgents[0];
            var result = await ExecuteAgentSafeAsync(agent, tenantId, userId, query, ct);
            if (result != null) results.Add(result);
        }
        else
        {
            // Multiple agents — parallel execution
            var tasks = selectedAgents.Select(async s =>
            {
                return await ExecuteAgentSafeAsync(s.Agent, tenantId, userId, query, ct);
            }).ToList();

            var completed = await Task.WhenAll(tasks);
            results.AddRange(completed.Where(r => r != null)!);
        }

        // Step 4: Merge results into context
        return MergeResults(results);
    }

    /// <summary>
    /// Execute a single agent with error handling and timeout.
    /// </summary>
    private async Task<AgentExecutionResult?> ExecuteAgentSafeAsync(
        ISpecializedAgent agent, Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30s timeout per agent

            _logger.LogInformation("🤖 Executing [{Agent}]...", agent.AgentName);
            var result = await agent.ExecuteAsync(tenantId, userId, query, cts.Token);

            if (result.Success)
            {
                _logger.LogInformation("✅ [{Agent}] completed in {Elapsed}ms. Tools: [{Tools}]",
                    result.AgentName, result.Elapsed.TotalMilliseconds,
                    string.Join(", ", result.ToolsUsed));
            }
            else
            {
                _logger.LogWarning("❌ [{Agent}] failed: {Error}", result.AgentName, result.ErrorMessage);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⏱️ [{Agent}] timed out after 30s", agent.AgentName);
            return AgentExecutionResult.Fail(agent.AgentName, "Timeout after 30 seconds");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 [{Agent}] unexpected error", agent.AgentName);
            return AgentExecutionResult.Fail(agent.AgentName, ex.Message);
        }
    }

    /// <summary>
    /// Merge multiple agent results into a single context string.
    /// </summary>
    private string MergeResults(List<AgentExecutionResult> results)
    {
        if (!results.Any()) return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("\n--- Multi-Agent Results ---");

        foreach (var result in results.Where(r => r.Success))
        {
            builder.AppendLine($"[{result.AgentName} — Tools: {string.Join(", ", result.ToolsUsed)}]:");
            builder.AppendLine(result.ResultContent);
            builder.AppendLine("---");
        }

        // Log failures as warnings in context
        foreach (var result in results.Where(r => !r.Success))
        {
            builder.AppendLine($"[{result.AgentName} — FAILED: {result.ErrorMessage}]");
        }

        builder.AppendLine("--- End Multi-Agent ---");
        return builder.ToString();
    }

    /// <summary>
    /// Get a summary of all registered agents and their capabilities.
    /// Useful for the routing/reasoning step.
    /// </summary>
    public string GetAgentDirectory()
    {
        var lines = _agents.Select(a => $"- {a.AgentName}: {a.Description} (Categories: {string.Join(", ", a.SupportedCategories)})");
        return string.Join("\n", lines);
    }
}
