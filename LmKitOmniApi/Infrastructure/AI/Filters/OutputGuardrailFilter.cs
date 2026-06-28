using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Filters;

/// <summary>
/// Output guardrail filter — validates and sanitizes AI model output before delivery.
/// Prevents data leakage, PII exposure, and policy violations.
/// </summary>
public class OutputGuardrailFilter : IAgentFilter
{
    private readonly IPromptGuardService _promptGuard;
    private readonly ILogger<OutputGuardrailFilter> _logger;

    public int Order => 90; // Runs late in the pipeline

    public OutputGuardrailFilter(IPromptGuardService promptGuard, ILogger<OutputGuardrailFilter> logger)
    {
        _promptGuard = promptGuard;
        _logger = logger;
    }

    public Task<AgentFilterResult> OnInputAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        // Output filter doesn't process inputs
        return Task.FromResult(AgentFilterResult.Pass(context.ProcessedInput));
    }

    public async Task<AgentFilterResult> OnOutputAsync(AgentFilterContext context, CancellationToken ct = default)
    {
        var output = context.Output ?? string.Empty;
        
        // Step 1: Check for data leakage via PromptGuard
        var guardResult = await _promptGuard.AnalyzeOutputAsync(output, ct);
        
        if (!guardResult.IsSafe)
        {
            _logger.LogWarning(
                "🛡️ Output sanitized by guardrail. Threats: [{Threats}]",
                string.Join(", ", guardResult.Detections.Select(d => d.ThreatType)));
            
            // Redact the problematic output rather than blocking entirely
            output = RedactSensitiveContent(output, guardResult);
        }

        // Step 2: Enforce output length limits
        const int MaxOutputLength = 16000;
        if (output.Length > MaxOutputLength)
        {
            output = output.Substring(0, MaxOutputLength) + "\n\n[Response truncated]";
        }

        var result = AgentFilterResult.Pass(output);
        if (guardResult.Detections.Count > 0)
        {
            result.Warnings = guardResult.Detections
                .Select(d => $"[OutputGuardrail] {d.ThreatType}: {d.Description}")
                .ToList();
        }

        return result;
    }

    private string RedactSensitiveContent(string output, PromptGuardResult guardResult)
    {
        var redacted = output;
        
        foreach (var detection in guardResult.Detections)
        {
            switch (detection.ThreatType)
            {
                case "CredentialLeakage":
                    // Redact anything that looks like credentials
                    redacted = System.Text.RegularExpressions.Regex.Replace(
                        redacted,
                        @"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN)\s*[:=]\s*\S+",
                        "$1: [REDACTED]");
                    break;
                    
                case "PIILeakage":
                    // Redact SSN patterns
                    redacted = System.Text.RegularExpressions.Regex.Replace(
                        redacted,
                        @"\b\d{3}[-.\s]?\d{2}[-.\s]?\d{4}\b",
                        "[SSN REDACTED]");
                    break;
                    
                case "SystemPromptLeakage":
                    // Add a disclaimer instead of redacting
                    redacted += "\n\n⚠️ *Lưu ý: Một số nội dung có thể đã bị lọc vì lý do bảo mật.*";
                    break;
            }
        }

        return redacted;
    }
}
