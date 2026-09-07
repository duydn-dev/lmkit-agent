using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Filters;

/// <summary>
/// Output guardrail filter — validates and sanitizes AI model output before delivery.
/// Prevents data leakage, PII exposure, and policy violations.
/// The pattern TEXTS declared here are the SINGLE SOURCE OF TRUTH for every stage of
/// the output guardrail: <c>PromptGuardService.AnalyzeOutputAsync</c> compiles them to
/// DETECT, the full-text pass below compiles them to REDACT, and the token-streaming
/// holdback gate (<c>StreamingGuardrailGate</c>) applies exactly these transforms — so
/// streamed content can never contain something the full pass would have removed.
///
/// <para><b>Why one string per class and not two patterns.</b> Detection and redaction
/// used to be two hand-maintained pattern sets, and they had drifted: the credential
/// redactor made the <c>':'</c>/<c>'='</c> separator OPTIONAL while the credential
/// detector made it MANDATORY. Redaction only runs once detection has fired, so the
/// narrower copy decided *whether* anything was scrubbed and the wider one decided *how
/// much*. <c>SECRET_KEY hunter2xyz</c> was therefore emitted to the user verbatim —
/// unless some unrelated credential in the same answer happened to carry a colon, at
/// which point the wider redactor scrubbed it after all. Same secret, opposite outcome,
/// decided by unrelated text elsewhere in the answer. Detection being NARROWER than
/// redaction is the security bug; a single shared string makes the two identical, so
/// the gap cannot reopen.</para>
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

    // ── Threat-class pattern texts: ONE definition per class, compiled by the
    //    detector (PromptGuardService.LeakagePatterns) and by the redactors below.
    //    Adding a class means adding a text here, never a second copy elsewhere.

    /// <summary>Credential keyword set — the vocabulary a leak is announced with.</summary>
    private const string CredentialKeywords = @"API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER";

    /// <summary>
    /// Characters a credential VALUE may be built from: a superset of hex, base64url,
    /// dot-separated JWTs and prefixed keys ("sk-live-…", "ghp_…"), and a strict subset
    /// of <c>\S</c> — which is what keeps the whole pattern inside the streaming gate's
    /// hold rule (it walks back over a trailing <c>\S+</c>).
    /// </summary>
    private const string CredentialValueChar = @"[A-Za-z0-9._+/=~-]";

    /// <summary>
    /// THE definition of a credential-shaped span. Two alternatives:
    ///
    /// <list type="number">
    /// <item><b>Explicit assignment</b> — <c>KEYWORD [:=] value</c>, value <c>\S+</c>.
    /// Exactly the old DETECTION pattern, unchanged: the separator is proof of intent.
    /// Folding BEARER into the shared keyword set widens this branch slightly —
    /// <c>Bearer: x</c> and <c>Bearer=x</c> used to fall between the two old detection
    /// patterns (the keyword one had no BEARER alternative, the BEARER one demanded
    /// whitespace) and are now covered.</item>
    /// <item><b>No separator</b> — <c>KEYWORD value</c>, admitted only when the value
    /// LOOKS like a secret: at least 8 <see cref="CredentialValueChar"/>s of which at
    /// least one is a DIGIT. This is the branch that closes the leak; the digit
    /// requirement is what keeps it off ordinary prose, where the word after
    /// "password" / "token" / "Bearer" is an English or Vietnamese word. The
    /// measured cost is in <c>CredentialGuardrailCorpusTests</c>.</item>
    /// </list>
    ///
    /// <para>The <c>\b</c> after the keyword is new and NARROWS both stages: the old
    /// redactor treated <c>TOKENISED</c> and <c>PASSWORDLESS</c> as credential spans
    /// (<c>TOKEN</c> + empty separator + <c>ISED</c>) and mangled them whenever anything
    /// else in the same answer tripped the detector. There is deliberately no LEADING
    /// <c>\b</c>, so <c>MYPASSWORD=…</c> stays covered.</para>
    ///
    /// <para><b>Residual, stated rather than hidden:</b> a separator-less value with no
    /// digit at all (<c>password correcthorsebatterystaple</c>) is not detected. The old
    /// <c>\bBEARER\s+\S+</c> detection pattern did catch that shape for BEARER alone —
    /// and fired on "Bearer authentication", "Bearer tokens travel…" and every other
    /// sentence about the scheme, which then rewrote every keyword-plus-word span in the
    /// whole answer. Narrowing that branch is a deliberate, measured trade.</para>
    ///
    /// <para>Group 1 is the keyword and MUST stay group 1: the replacement template
    /// <c>"$1: [REDACTED]"</c> below and <c>StreamingGuardrailGate</c>'s equivalent
    /// <c>MatchEvaluator</c> both read it.</para>
    ///
    /// <para>Both branches are MONOTONE under appending — no end anchors, every
    /// assertion decided by characters inside the matched span — which is what the
    /// streaming gate's latching argument requires, and the language is a strict subset
    /// of the pattern it replaces, so the gate's backward hold scan remains a valid
    /// over-approximation.</para>
    /// </summary>
    public const string CredentialPatternText =
        @"(?i)(" + CredentialKeywords + @")\b(?:"
        + @"\s*[:=]\s*\S+"
        + @"|\s+(?=" + CredentialValueChar + @"{8,})(?=" + CredentialValueChar + @"*[0-9])"
        + CredentialValueChar + @"+)";

    /// <summary>
    /// US SSN-shaped number groups. A separated group (<c>123-45-6789</c>,
    /// <c>123 45 6789</c>) may now be glued to a word — <c>123-45-6789The</c> matched
    /// neither stage before (known issue #3) — but must still not be adjacent to another
    /// DIGIT, which is what keeps a 9-digit window out of a longer number. The
    /// separator-less form keeps its <c>\b</c>: relaxing that one would claim any run of
    /// nine digits inside a hex digest or identifier. Longest match is 11 characters,
    /// which <c>StreamingGuardrailGate.MaxSsnMatchChars</c> depends on.
    /// </summary>
    public const string SsnPatternText =
        @"(?:\b\d{3}\d{2}\d{4}\b"
        + @"|(?<![0-9])\d{3}[-.\s]\d{2}[-.\s]?\d{4}(?![0-9])"
        + @"|(?<![0-9])\d{3}[-.\s]?\d{2}[-.\s]\d{4}(?![0-9]))";

    /// <summary>
    /// Email addresses. The trailing <c>\b</c> is gone: it made
    /// <c>bob@example.com123</c> and <c>bob@example.com_x</c> match nothing at all,
    /// while adding no protection (a letter glued to the TLD was already swallowed by
    /// the greedy <c>[A-Za-z]{2,}</c>). Dropping it also makes a match strictly more
    /// stable under appending, which the streaming gate relies on.
    /// </summary>
    public const string EmailPatternText =
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}";

    /// <summary>Credential-shaped spans (API keys, secrets, passwords, tokens, bearer values).</summary>
    public static readonly Regex CredentialRedactionPattern = new(
        CredentialPatternText,
        RegexOptions.Compiled);

    /// <summary>US SSN-shaped number groups.</summary>
    public static readonly Regex SsnRedactionPattern = new(
        SsnPatternText,
        RegexOptions.Compiled);

    /// <summary>Email addresses.</summary>
    public static readonly Regex EmailRedactionPattern = new(
        EmailPatternText,
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
