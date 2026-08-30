using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Filters;

/// <summary>
/// Output guardrail filter — validates and sanitizes AI model output before delivery.
/// Prevents data leakage, PII exposure, and policy violations.
/// The redaction patterns and pure helpers exposed by this class are the SINGLE
/// SOURCE OF TRUTH for output redaction: both the full-text pass here and the
/// token-streaming holdback gate in <c>AgentOrchestrator</c> apply exactly these
/// transforms, so streamed content can never contain something the full pass
/// would have removed.
/// </summary>
public class OutputGuardrailFilter : IAgentFilter
{
    private readonly IPromptGuardService _promptGuard;
    private readonly ILogger<OutputGuardrailFilter> _logger;

    public int Order => 90; // Runs late in the pipeline

    /// <summary>Maximum characters of (post-redaction) model output delivered to the client.</summary>
    public const int MaxOutputLength = 16000;

    /// <summary>Appended when output exceeds <see cref="MaxOutputLength"/>.</summary>
    public const string TruncationNotice = "\n\n[Response truncated]";

    /// <summary>Appended once when SystemPromptLeakage is detected (content kept, disclaimer added).</summary>
    public const string SystemPromptLeakageNotice = "\n\n⚠️ *Lưu ý: Một số nội dung có thể đã bị lọc vì lý do bảo mật.*";

    // ── Redaction patterns (one source of truth; keep in sync with the detection
    //    patterns in PromptGuardService.AnalyzeOutputAsync) ──
    // The credential pattern is deliberately broader than its detection counterpart
    // (':'/'=' optional here, mandatory there): once CredentialLeakage is detected
    // anywhere in the text, every credential-shaped span gets scrubbed.

    /// <summary>Credential-shaped spans (API keys, secrets, passwords, tokens, bearer values).</summary>
    public static readonly Regex CredentialRedactionPattern = new(
        @"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER)\s*[:=]?\s*\S+",
        RegexOptions.Compiled);

    /// <summary>US SSN-shaped number groups.</summary>
    public static readonly Regex SsnRedactionPattern = new(
        @"\b\d{3}[-.\s]?\d{2}[-.\s]?\d{4}\b",
        RegexOptions.Compiled);

    /// <summary>Email addresses.</summary>
    public static readonly Regex EmailRedactionPattern = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

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
            output = RedactForDetectedThreats(output, guardResult.Detections.Select(d => d.ThreatType));
        }

        // Step 2: Enforce output length limits
        if (output.Length > MaxOutputLength)
        {
            output = output.Substring(0, MaxOutputLength) + TruncationNotice;
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

    /// <summary>Scrubs every credential-shaped span. Idempotent.</summary>
    public static string RedactCredentialContent(string text)
        => CredentialRedactionPattern.Replace(text, "$1: [REDACTED]");

    /// <summary>Scrubs SSN- and email-shaped spans (in that order). Idempotent.</summary>
    public static string RedactPiiContent(string text)
    {
        var redacted = SsnRedactionPattern.Replace(text, "[SSN REDACTED]");
        return EmailRedactionPattern.Replace(redacted, "[EMAIL REDACTED]");
    }

    /// <summary>
    /// Applies the per-threat-type redaction for a detection list, in detection
    /// order — exactly the transform the full output pass performs. Pure and
    /// deterministic; shared with the streaming holdback gate so both paths
    /// produce identical text for identical inputs.
    /// </summary>
    public static string RedactForDetectedThreats(string output, IEnumerable<string> detectedThreatTypes)
    {
        var redacted = output;

        foreach (var threatType in detectedThreatTypes)
        {
            switch (threatType)
            {
                case ThreatTypes.CredentialLeakage:
                    // Redact anything that looks like credentials
                    redacted = RedactCredentialContent(redacted);
                    break;

                case ThreatTypes.PIILeakage:
                    // Redact SSN and email patterns
                    redacted = RedactPiiContent(redacted);
                    break;

                case ThreatTypes.SystemPromptLeakage:
                    // Add a disclaimer instead of redacting
                    redacted += SystemPromptLeakageNotice;
                    break;
            }
        }

        return redacted;
    }
}
