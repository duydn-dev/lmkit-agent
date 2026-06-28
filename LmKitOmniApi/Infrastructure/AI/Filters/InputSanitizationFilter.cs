using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Filters;

/// <summary>
/// Input sanitization filter — first line of defense in the filter pipeline.
/// Cleans and validates user input before it reaches the AI model.
/// </summary>
public class InputSanitizationFilter : IAgentFilter
{
    private readonly IPromptGuardService _promptGuard;
    private readonly ILogger<InputSanitizationFilter> _logger;

    public int Order => 10; // Runs early in the pipeline

    public InputSanitizationFilter(IPromptGuardService promptGuard, ILogger<InputSanitizationFilter> logger)
    {
        _promptGuard = promptGuard;
        _logger = logger;
    }

    public async Task<AgentFilterResult> OnInputAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        var input = context.ProcessedInput;
        
        // Step 1: Basic sanitization
        input = SanitizeBasic(input);
        
        // Step 2: Prompt injection detection
        var guardResult = await _promptGuard.AnalyzeInputAsync(input, ct);
        
        if (!guardResult.IsSafe)
        {
            _logger.LogWarning(
                "🛡️ Input blocked by PromptGuard. Risk: {Risk:P0}, Level: {Level}, Threats: [{Threats}]",
                guardResult.RiskScore, 
                guardResult.RiskLevel,
                string.Join(", ", guardResult.Detections.Select(d => d.ThreatType)));

            return AgentFilterResult.Block(
                $"Yêu cầu của bạn đã bị từ chối vì lý do bảo mật. (Risk Level: {guardResult.RiskLevel})");
        }

        // Pass with warnings if any low-risk detections
        var result = AgentFilterResult.Pass(input);
        if (guardResult.Detections.Count > 0)
        {
            result.Warnings = guardResult.Detections
                .Select(d => $"[{d.ThreatType}] {d.Description} (Confidence: {d.Confidence:P0})")
                .ToList();
        }

        return result;
    }

    public Task<AgentFilterResult> OnOutputAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        // Input filter doesn't process outputs
        return Task.FromResult(AgentFilterResult.Pass(context.Output ?? string.Empty));
    }

    private string SanitizeBasic(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        
        // Remove null bytes
        input = input.Replace("\0", "");
        
        // Normalize whitespace (but preserve newlines)
        input = System.Text.RegularExpressions.Regex.Replace(input, @"[ \t]+", " ");
        
        // Trim excessive newlines (max 3 consecutive)
        input = System.Text.RegularExpressions.Regex.Replace(input, @"\n{4,}", "\n\n\n");
        
        // Limit input length (prevent context overflow attacks)
        const int MaxInputLength = 8000;
        if (input.Length > MaxInputLength)
        {
            input = input.Substring(0, MaxInputLength) + "\n[Input truncated for safety]";
        }
        
        return input.Trim();
    }
}
