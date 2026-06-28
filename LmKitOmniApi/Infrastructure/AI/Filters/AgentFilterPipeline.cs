using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Filters;

/// <summary>
/// Orchestrates the filter pipeline — runs all registered IAgentFilter
/// in order for both input and output processing.
/// Inspired by console_net/ai-agents/filter-pipeline.
/// </summary>
public class AgentFilterPipeline
{
    private readonly IEnumerable<IAgentFilter> _filters;
    private readonly ILogger<AgentFilterPipeline> _logger;

    public AgentFilterPipeline(IEnumerable<IAgentFilter> filters, ILogger<AgentFilterPipeline> logger)
    {
        _filters = filters.OrderBy(f => f.Order);
        _logger = logger;
    }

    /// <summary>
    /// Run all input filters in order. Returns the final processed input or a block result.
    /// </summary>
    public async Task<AgentFilterResult> RunInputFiltersAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        context.ProcessedInput = context.OriginalInput;
        var allWarnings = new List<string>();

        foreach (var filter in _filters)
        {
            var result = await filter.OnInputAsync(context, ct);
            
            if (result.IsBlocked)
            {
                _logger.LogWarning("🚫 Input blocked by filter {Filter}: {Reason}", 
                    filter.GetType().Name, result.BlockReason);
                return result;
            }

            context.ProcessedInput = result.ProcessedContent;
            allWarnings.AddRange(result.Warnings);
        }

        return new AgentFilterResult
        {
            ProcessedContent = context.ProcessedInput,
            Warnings = allWarnings
        };
    }

    /// <summary>
    /// Run all output filters in order. Returns the final processed output.
    /// </summary>
    public async Task<AgentFilterResult> RunOutputFiltersAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        var allWarnings = new List<string>();

        foreach (var filter in _filters)
        {
            var result = await filter.OnOutputAsync(context, ct);
            
            if (result.IsBlocked)
            {
                _logger.LogWarning("🚫 Output blocked by filter {Filter}: {Reason}",
                    filter.GetType().Name, result.BlockReason);
                return result;
            }

            context.Output = result.ProcessedContent;
            allWarnings.AddRange(result.Warnings);
        }

        return new AgentFilterResult
        {
            ProcessedContent = context.Output ?? string.Empty,
            Warnings = allWarnings
        };
    }
}
