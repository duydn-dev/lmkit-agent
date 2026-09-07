using System.Text;
using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Incremental streaming emission gate for chat responses: releases model output
/// as a post-redaction prefix while guaranteeing the released prefix never
/// diverges from the full end-of-stream guardrail pass.
///
/// <para><b>Hold rule.</b> Let <c>raw</c> be the text accumulated so far and
/// <c>L = raw.Length</c>. The gate computes a hold distance <c>d</c> and treats
/// <c>raw[0 .. L-d)</c> as <i>settled</i>: no continuation of the stream can change
/// the guardrail-processed text over that region. <c>d</c> is the smallest value
/// that covers, for every redaction pattern <c>P</c>
/// (<see cref="OutputGuardrailFilter.CredentialRedactionPattern"/>,
/// <see cref="OutputGuardrailFilter.SsnRedactionPattern"/>,
/// <see cref="OutputGuardrailFilter.EmailRedactionPattern"/>), every index <c>s</c>
/// at which <c>raw[s..L]</c> is still a viable <i>prefix</i> of a match of <c>P</c>:</para>
/// <list type="bullet">
/// <item>SSN — the pattern is fully bounded at 11 characters, so a match that is not
/// yet complete starts no earlier than <c>L-10</c>; the gate holds 11.</item>
/// <item>Email — every character of a match lies in
/// <c>[A-Za-z0-9._%+@-]</c>, so a partial match at the live edge is contained in the
/// maximal run of those characters ending at <c>L</c>; the gate holds that run.</item>
/// <item>Credential — both branches of
/// <see cref="OutputGuardrailFilter.CredentialPatternText"/> (keyword +
/// <c>\s*[:=]\s*</c> + <c>\S+</c>, and keyword + <c>\s+</c> + a value run) are
/// contained in <c>keyword</c> + <c>\s*[:=]?\s*</c> + <c>\S+</c>, so a partial match
/// is at most: the trailing non-whitespace run (a candidate value, or a half-typed
/// keyword), the preceding separator, and a keyword ending there (≤ 10 characters, and
/// only when one actually does). The walk-back is an over-approximation of the
/// pattern on purpose — it stays valid as long as the pattern stays inside that
/// shape.</item>
/// </list>
///
/// <para><b>Why the scans are not clamped.</b> The email local part and the
/// credential <c>\S+</c> are unbounded quantifiers, so the worst-case hold is
/// unbounded. Capping the backward scan at some constant (the old rule's flat 512)
/// would mean releasing text the rule cannot vouch for: an unbroken run of
/// email-legal characters longer than the cap that finally turns out to be an
/// address diverges from the full pass, and the caller then SUPPRESSES the tail —
/// the user loses the end of their answer. The scans are plain character loops over
/// text already in hand, no more expensive than the regex passes this class already
/// runs per attempt, so they are left exact.</para>
///
/// <para><b>The cost of that choice</b>, stated plainly: on pathological output — an
/// unbroken run with no whitespace, e.g. a 5 000-character minified line — the hold
/// spans the whole run and nothing streams until the run ends. Such an answer is
/// still delivered in full, by the caller's end-of-stream flush. A stall is the
/// deliberate trade against a truncated answer. Ordinary prose holds 11–20
/// characters.</para>
/// </summary>
internal sealed class StreamingGuardrailGate
{
    /// <summary>
    /// Longest possible <see cref="OutputGuardrailFilter.SsnRedactionPattern"/> match:
    /// <c>3 + 1 + 2 + 1 + 4</c>. The pattern has no unbounded quantifier.
    /// </summary>
    private const int MaxSsnMatchChars = 11;

    /// <summary>Longest credential keyword ("SECRET_KEY" / "SECRET KEY").</summary>
    private const int MaxCredentialKeywordChars = 10;

    /// <summary>Shortest credential keyword ("TOKEN").</summary>
    private const int MinCredentialKeywordChars = 5;

    /// <summary>
    /// Emit attempts run once at least this much new raw text has arrived.
    /// The LM-Kit callback delivers a few characters per segment; batching keeps the
    /// O(text) view rebuild off the per-token hot path while still flushing several
    /// times per second.
    /// </summary>
    private const int EmitStrideChars = 32;

    /// <summary>
    /// Stride used while the answer is still short. A two-line reply must not sit
    /// behind a 32-character batching window, and the rebuild is cheap while the text
    /// is short (a 512-char answer costs at most 64 attempts over ≤ 512 chars).
    /// Attempting more often is never a safety question — the hold rule alone decides
    /// what may be released.
    /// </summary>
    private const int ShortAnswerStrideChars = 8;

    /// <summary>Length past which the steady-state <see cref="EmitStrideChars"/> takes over.</summary>
    private const int ShortAnswerChars = 512;

    /// <summary>
    /// Detector re-scan cadence over the settled region. The hold rule — not this
    /// cadence — is what prevents premature emission, so lag here only delays the
    /// release of held spans, never safety.
    /// </summary>
    private const int DetectionStrideChars = 256;

    /// <summary>
    /// Keyword-only prefix of <see cref="OutputGuardrailFilter.CredentialRedactionPattern"/>:
    /// the full pattern's trailing separator-and-value may only complete long after the
    /// keyword has left the hold window, so while the credential class is unlatched
    /// the cap anchors on the keyword itself. Deliberately WIDER than the shared
    /// pattern's keyword clause (no <c>\b</c>) — capping earlier than necessary only
    /// delays a release, whereas capping later than necessary would release text the
    /// end-of-stream pass might still rewrite.
    /// </summary>
    private static readonly Regex CredentialHoldPattern = new(
        @"(?i)API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER",
        RegexOptions.Compiled);

    /// <summary>Same keyword set, anchored, for "does a keyword end exactly here?".</summary>
    private static readonly Regex CredentialKeywordAnchored = new(
        @"\A(?i:API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER)\z",
        RegexOptions.Compiled);

    private readonly IPromptGuardService _promptGuard;
    private readonly StringBuilder _raw = new();
    private string _emitted = string.Empty;
    private int _lastEmitAttemptRawLength;
    private int _lastAnalyzedStableLength;
    private bool _credentialClassLatched;
    private bool _piiClassLatched;
    private bool _diverged;

    public StreamingGuardrailGate(IPromptGuardService promptGuard)
    {
        _promptGuard = promptGuard;
    }

    /// <summary>Complete raw model output accumulated so far (pre-redaction).</summary>
    public string RawText => _raw.ToString();

    /// <summary>Everything emitted downstream so far (a post-redaction prefix).</summary>
    public string EmittedText => _emitted;

    /// <summary>
    /// Appends one model segment and returns the next chunk that is safe to
    /// emit (empty when nothing new can be released yet).
    /// </summary>
    public async Task<string> AppendAndTryEmitAsync(string text, CancellationToken ct)
    {
        _raw.Append(text);
        if (_diverged)
            return string.Empty;

        var stride = _raw.Length < ShortAnswerChars ? ShortAnswerStrideChars : EmitStrideChars;
        if (_raw.Length - _lastEmitAttemptRawLength < stride)
            return string.Empty;
        _lastEmitAttemptRawLength = _raw.Length;

        var raw = _raw.ToString();

        // 1. Hold boundary: raw[0 .. settledRawLength) is immune to every possible
        //    continuation of the stream (see the class remarks for the argument).
        var hold = ComputeHoldDistance(raw);
        var settledRawLength = raw.Length - hold;
        if (settledRawLength <= 0)
            return string.Empty;

        // 2. Latch threat classes from the settled region only — a match that lies
        //    there cannot be altered or dissolved by later appends, so a latched
        //    class is also detected by the end-of-stream full pass.
        await LatchThreatClassesAsync(raw, settledRawLength, ct);

        // 3. Conditionally redacted view — the same class transforms, in the same
        //    order, that OutputGuardrailFilter applies to the full text. Only
        //    matches lying wholly inside the settled region are replaced; anything
        //    reaching into the unsettled tail is left verbatim, which keeps the last
        //    `unsettled` characters of the view byte-identical to the raw tail and
        //    therefore keeps `view.Length - unsettled` the exact image of the
        //    settled boundary.
        var view = raw;
        var unsettled = hold;
        if (_credentialClassLatched)
            view = ReplaceSettled(view, OutputGuardrailFilter.CredentialRedactionPattern, CredentialReplacement, ref unsettled);
        if (_piiClassLatched)
        {
            view = ReplaceSettled(view, OutputGuardrailFilter.SsnRedactionPattern, _ => "[SSN REDACTED]", ref unsettled);
            view = ReplaceSettled(view, OutputGuardrailFilter.EmailRedactionPattern, _ => "[EMAIL REDACTED]", ref unsettled);
        }

        // 4. Emission cap: the settled boundary, plus the full pass's truncation cap
        //    (the caller releases the truncation marker itself).
        var safeLength = Math.Min(view.Length - unsettled, OutputGuardrailFilter.MaxOutputLength);
        if (safeLength <= _emitted.Length)
            return string.Empty;

        // 5. While a class is still unlatched its redaction has NOT been applied to
        //    the view, so emission must stop before any span the end-of-stream pass
        //    could rewrite once the class is detected.
        if (!_credentialClassLatched)
            safeLength = CapBeforeEarliestMatch(view, CredentialHoldPattern, safeLength);
        if (!_piiClassLatched)
        {
            safeLength = CapBeforeEarliestMatch(view, OutputGuardrailFilter.SsnRedactionPattern, safeLength);
            safeLength = CapBeforeEarliestMatch(view, OutputGuardrailFilter.EmailRedactionPattern, safeLength);
        }
        if (safeLength <= _emitted.Length)
            return string.Empty;

        // 6. Defence in depth: if the recomputed view ever contradicts what was
        //    already released, stop emitting instead of compounding the error. The
        //    caller's end-of-stream prefix check then reports the divergence.
        if (!view.AsSpan().StartsWith(_emitted.AsSpan(), StringComparison.Ordinal))
        {
            _diverged = true;
            return string.Empty;
        }

        var chunk = view.Substring(_emitted.Length, safeLength - _emitted.Length);
        _emitted += chunk;
        return chunk;
    }

    // ── hold rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// Characters back from the live edge that must be withheld: the maximum, over
    /// the three redaction patterns, of how far a still-incomplete match could reach.
    /// Exact — deliberately not clamped; see the class remarks.
    /// </summary>
    internal static int ComputeHoldDistance(string raw)
    {
        var hold = MaxSsnMatchChars;

        var emailHold = raw.Length - EmailTailRunStart(raw);
        if (emailHold > hold) hold = emailHold;

        var credentialHold = raw.Length - CredentialTailStart(raw);
        if (credentialHold > hold) hold = credentialHold;

        return Math.Min(hold, raw.Length);
    }

    /// <summary>
    /// Start of the maximal run of email-legal characters ending at the live edge.
    /// Every character of an <see cref="OutputGuardrailFilter.EmailRedactionPattern"/>
    /// match lies in this class, so a match still being typed cannot begin earlier.
    /// </summary>
    private static int EmailTailRunStart(string raw)
    {
        var i = raw.Length;
        while (i > 0 && IsEmailChar(raw[i - 1])) i--;
        return i;
    }

    private static bool IsEmailChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
           || c == '.' || c == '_' || c == '%' || c == '+' || c == '-' || c == '@';

    /// <summary>
    /// Earliest index at which an in-progress
    /// <see cref="OutputGuardrailFilter.CredentialRedactionPattern"/> match could
    /// begin: walk back over the trailing <c>\S+</c> candidate (which also covers a
    /// half-typed keyword), then over <c>\s*[:=]?\s*</c>, then over a keyword if one
    /// really ends there.
    /// </summary>
    private static int CredentialTailStart(string raw)
    {
        var i = raw.Length;
        while (i > 0 && !char.IsWhiteSpace(raw[i - 1])) i--;      // \S+ (value, or a half-typed keyword)

        var j = i;
        while (j > 0 && char.IsWhiteSpace(raw[j - 1])) j--;       // trailing \s*
        if (j > 0 && (raw[j - 1] == ':' || raw[j - 1] == '='))
        {
            j--;                                                   // [:=]?
            while (j > 0 && char.IsWhiteSpace(raw[j - 1])) j--;   // leading \s*
        }

        return CredentialKeywordStart(raw, j);
    }

    /// <summary>
    /// Start index of the credential keyword ending exactly at <paramref name="end"/>,
    /// or <paramref name="end"/> when no keyword ends there. Longest first, so the
    /// earliest start wins.
    /// </summary>
    private static int CredentialKeywordStart(string raw, int end)
    {
        for (var len = MaxCredentialKeywordChars; len >= MinCredentialKeywordChars; len--)
        {
            var start = end - len;
            if (start < 0) continue;
            if (CredentialKeywordAnchored.IsMatch(raw.Substring(start, len)))
                return start;
        }
        return end;
    }

    // ── view construction ────────────────────────────────────────────────────

    /// <summary>
    /// Byte-equivalent of <c>Replace(text, "$1: [REDACTED]")</c> used by
    /// <see cref="OutputGuardrailFilter.RedactCredentialContent"/>.
    /// </summary>
    private static string CredentialReplacement(Match m) => m.Groups[1].Value + ": [REDACTED]";

    /// <summary>
    /// Applies <paramref name="pattern"/> only to matches that end at or before the
    /// settled boundary <c>text.Length - unsettled</c>. A match straddling the
    /// boundary pulls the boundary back to its own start, so no replacement ever
    /// rewrites a character of the unsettled tail — which is what makes
    /// <c>view.Length - unsettled</c> the exact image of the settled boundary after
    /// an arbitrary chain of these transforms.
    /// </summary>
    private static string ReplaceSettled(string text, Regex pattern, MatchEvaluator evaluator, ref int unsettled)
    {
        var boundary = text.Length - unsettled;

        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index >= boundary) break;
            if (m.Index + m.Length > boundary) { boundary = m.Index; break; }
        }

        unsettled = text.Length - boundary;
        var limit = boundary;
        return pattern.Replace(text, m => m.Index + m.Length <= limit ? evaluator(m) : m.Value);
    }

    // ── latching ─────────────────────────────────────────────────────────────

    private async Task LatchThreatClassesAsync(string raw, int settledRawLength, CancellationToken ct)
    {
        // PII: latch straight off the shared redaction patterns, positionally, on the
        // FULL raw. A match ending at or before the settled boundary has its trailing
        // \b decided by a character that can never change, so it survives into the
        // end-of-stream pass — whereas asking the detector about raw[..settled] alone
        // would let an artificial end-of-string boundary latch a match that a later
        // character dissolves. PromptGuardService compiles these very pattern texts, so
        // a match here guarantees the full pass reports PIILeakage.
        if (!_piiClassLatched
            && (HasMatchEndingWithin(OutputGuardrailFilter.SsnRedactionPattern, raw, settledRawLength)
                || HasMatchEndingWithin(OutputGuardrailFilter.EmailRedactionPattern, raw, settledRawLength)))
        {
            _piiClassLatched = true;
        }

        if (_credentialClassLatched)
            return;

        // Credential: the detector now compiles the SAME pattern text as the redactor
        // (OutputGuardrailFilter.CredentialPatternText), so asking it about the settled
        // region is exactly asking "would the full pass redact here?". The pattern is
        // monotone under appending — no end anchors, every assertion decided inside the
        // matched span, trailing quantifiers that can only grow — so a match inside the
        // settled region is still a match at end of stream.
        var due = settledRawLength - _lastAnalyzedStableLength;
        if (due < DetectionStrideChars && !(raw.Length < ShortAnswerChars && due >= ShortAnswerStrideChars))
            return;

        _lastAnalyzedStableLength = settledRawLength;
        var guard = await _promptGuard.AnalyzeOutputAsync(raw[..settledRawLength], ct);
        foreach (var detection in guard.Detections)
        {
            if (detection.ThreatType == ThreatTypes.CredentialLeakage) _credentialClassLatched = true;
            // PII is latched positionally above; SystemPromptLeakage only appends an
            // end-of-text disclaimer, and the full pass owns the text end.
        }
    }

    private static bool HasMatchEndingWithin(Regex pattern, string raw, int boundary)
    {
        var m = pattern.Match(raw);
        while (m.Success && m.Index < boundary)
        {
            if (m.Index + m.Length <= boundary) return true;
            m = m.NextMatch();
        }
        return false;
    }

    // ── caps ─────────────────────────────────────────────────────────────────

    private int CapBeforeEarliestMatch(string view, Regex pattern, int currentCap)
    {
        var match = pattern.Match(view, _emitted.Length);
        return match.Success && match.Index < currentCap ? match.Index : currentCap;
    }
}
