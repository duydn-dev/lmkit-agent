using System.Text;
using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;

namespace LmKitOmniApi.Infrastructure.AI;

// Extracted verbatim from AgentOrchestrator (same assembly, internal visibility
// unchanged) so the orchestrator file carries only orchestration logic. Behavior
// is byte-identical; see the original class remarks below.

/// <summary>
/// Incremental streaming emission gate for chat responses: releases model output
/// as a post-redaction prefix while guaranteeing the released prefix never
/// diverges from the full end-of-stream guardrail pass.
///
/// Known residual (accepted, logged fail-safe by the caller's prefix check):
/// degenerate constructs whose pattern membership is only decidable more than
/// <see cref="HoldbackChars"/> chars later — e.g. a 512+ char unbroken run
/// that finally turns out to be an "email", or a credential keyword followed
/// by 512+ chars of pure whitespace before its value. Real model output does
/// not produce these; if one ever occurs the caller suppresses the tail
/// rather than risking a leak or duplicate.
/// </summary>
internal sealed class StreamingGuardrailGate
{
    /// <summary>Unemitted tail always retained (see class remarks, point 1).</summary>
    private const int HoldbackChars = 512;

    /// <summary>
    /// Emit attempts run once at least this much new raw text has arrived.
    /// The LM-Kit callback delivers a few characters per segment; batching
    /// ~32 chars per flush keeps the O(text) view rebuild off the per-token
    /// hot path while still flushing several times per second.
    /// </summary>
    private const int EmitStrideChars = 32;

    /// <summary>
    /// Detector re-scan cadence over the stable region. The hold rule — not
    /// this cadence — is what prevents premature emission, so lag here only
    /// delays the release of held spans, never safety.
    /// </summary>
    private const int DetectionStrideChars = 256;

    /// <summary>
    /// Keyword-only prefix of <see cref="OutputGuardrailFilter.CredentialRedactionPattern"/>:
    /// the full pattern's trailing "\s*[:=]?\s*\S+" may only complete long
    /// after the keyword has left the holdback window, so the hold rule
    /// anchors on the keyword itself.
    /// </summary>
    private static readonly Regex CredentialHoldPattern = new(
        @"(?i)API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER",
        RegexOptions.Compiled);

    private readonly IPromptGuardService _promptGuard;
    private readonly StringBuilder _raw = new();
    private readonly StringBuilder _emitted = new();
    private int _lastEmitAttemptRawLength;
    private int _lastAnalyzedStableLength;
    private bool _credentialClassLatched;
    private bool _piiClassLatched;

    public StreamingGuardrailGate(IPromptGuardService promptGuard)
    {
        _promptGuard = promptGuard;
    }

    /// <summary>Complete raw model output accumulated so far (pre-redaction).</summary>
    public string RawText => _raw.ToString();

    /// <summary>Everything emitted downstream so far (a post-redaction prefix).</summary>
    public string EmittedText => _emitted.ToString();

    /// <summary>
    /// Appends one model segment and returns the next chunk that is safe to
    /// emit (empty when nothing new can be released yet).
    /// </summary>
    public async Task<string> AppendAndTryEmitAsync(string text, CancellationToken ct)
    {
        _raw.Append(text);
        if (_raw.Length - _lastEmitAttemptRawLength < EmitStrideChars)
            return string.Empty;
        _lastEmitAttemptRawLength = _raw.Length;

        var raw = _raw.ToString();

        // 1. Latch threat classes from the stable region only — a match fully
        //    inside it cannot be altered or dissolved by later appends, so a
        //    latched class is also detected by the end-of-stream full pass.
        var stableLength = raw.Length - HoldbackChars;
        if (!(_credentialClassLatched && _piiClassLatched)
            && stableLength - _lastAnalyzedStableLength >= DetectionStrideChars)
        {
            _lastAnalyzedStableLength = stableLength;
            var guard = await _promptGuard.AnalyzeOutputAsync(raw[..stableLength], ct);
            foreach (var detection in guard.Detections)
            {
                if (detection.ThreatType == ThreatTypes.CredentialLeakage) _credentialClassLatched = true;
                else if (detection.ThreatType == ThreatTypes.PIILeakage) _piiClassLatched = true;
                // SystemPromptLeakage only appends an end-of-text disclaimer;
                // the full pass owns the text end, so nothing to do mid-stream.
            }
        }

        // 2. Conditionally redacted view — the same class transforms, in the
        //    same order, that OutputGuardrailFilter applies to the full text.
        //    Credential replacements are append-stable as-is (they keep the
        //    keyword and never dissolve). SSN/email matches ending exactly at
        //    the live edge could still dissolve or extend, so those stay
        //    unreplaced until settled — they sit inside the holdback window,
        //    which keeps them unemittable meanwhile.
        var view = raw;
        if (_credentialClassLatched)
            view = OutputGuardrailFilter.RedactCredentialContent(view);
        if (_piiClassLatched)
        {
            view = ReplaceSettledMatches(OutputGuardrailFilter.SsnRedactionPattern, view, "[SSN REDACTED]");
            view = ReplaceSettledMatches(OutputGuardrailFilter.EmailRedactionPattern, view, "[EMAIL REDACTED]");
        }

        // 3. Emission cap: holdback from the live edge, plus the full pass's
        //    truncation cap (the caller releases the truncation marker).
        var safeLength = Math.Min(view.Length - HoldbackChars, OutputGuardrailFilter.MaxOutputLength);
        if (safeLength <= _emitted.Length)
            return string.Empty;

        // 4. Hold rule (see class remarks, point 3). The credential keyword
        //    hold applies only while unlatched: once latched, every keyword
        //    reaching the emit zone is part of a completed "$1: [REDACTED]"
        //    replacement in the view.
        if (!_credentialClassLatched)
            safeLength = CapBeforeEarliestMatch(view, CredentialHoldPattern, safeLength);
        if (!_piiClassLatched)
        {
            safeLength = CapBeforeEarliestMatch(view, OutputGuardrailFilter.SsnRedactionPattern, safeLength);
            safeLength = CapBeforeEarliestMatch(view, OutputGuardrailFilter.EmailRedactionPattern, safeLength);
        }
        if (safeLength <= _emitted.Length)
            return string.Empty;

        var chunk = view.Substring(_emitted.Length, safeLength - _emitted.Length);
        _emitted.Append(chunk);
        return chunk;
    }

    /// <summary>
    /// Applies <paramref name="pattern"/> replacements except for a match
    /// touching the very end of the text, where an append could still extend
    /// or dissolve it (e.g. "123-45-6789" gaining another digit). Such a
    /// match lies inside the holdback window, so deferring it never delays
    /// emittable content.
    /// </summary>
    private static string ReplaceSettledMatches(Regex pattern, string text, string replacement)
        => pattern.Replace(text, m => m.Index + m.Length >= text.Length ? m.Value : replacement);

    private int CapBeforeEarliestMatch(string view, Regex pattern, int currentCap)
    {
        var match = pattern.Match(view, _emitted.Length);
        return match.Success && match.Index < currentCap ? match.Index : currentCap;
    }
}
