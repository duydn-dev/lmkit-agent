using System.Text;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// The orchestrator's in-band SSE marker vocabulary, and how to strip it out of a
/// captured stream so what is stored as <see cref="Domain.Entities.AgentRun.Result"/> is
/// clean prose.
///
/// <para>Shared by the two places that drive a run and persist its answer — the first
/// pass (<c>StreamAgentRunCommandHandler</c>) and every continuation after an approval
/// (<c>AgentRunResumeService</c>). They must agree exactly: a marker left in a resumed
/// run's result would be re-parsed by the client as a live event, and — worse — a stray
/// <c>[HITL_APPROVAL_REQUIRED:…]</c> would make the agent-run page adopt a gate for an
/// approval that has already been answered.</para>
/// </summary>
internal static class AgentRunMarkers
{
    /// <summary>Prefix the orchestrator emits when a tool call parked on a human decision.</summary>
    public const string ApprovalRequired = "[HITL_APPROVAL_REQUIRED:";

    /// <summary>
    /// LINE-shaped markers: <c>[NAME]: …</c> running to the end of its line. The body is GREEDY
    /// on purpose, because the payload may itself contain <c>[</c>.
    ///
    /// <para>One regex used to cover both shapes and served neither. Its tail was
    /// <c>(?:\][\n\r]*|(?=\[)|$)</c> over a LAZY body, so a newline-terminated marker matched
    /// nothing at all: the body cannot cross a line break, and <c>$</c> only matches at end of
    /// input without <see cref="RegexOptions.Multiline"/>. <c>[STEP:{…}]</c> and
    /// <c>[FILE:{…}]</c> were stripped because they close with <c>]</c>, while every
    /// <c>[THINKING]:</c> line survived into the stored
    /// <see cref="Domain.Entities.AgentRun.Result"/>.</para>
    ///
    /// <para>Splitting the two marker shapes apart is what fixes it. A single lazy pattern also
    /// terminated on <c>(?=\[)</c>, i.e. at the first bracket INSIDE a payload — and
    /// <c>[WEB_SEARCH]:</c> always carries a JSON array, so its payload leaked through as raw
    /// JSON. Both bugs failed safely: noise left in, never prose eaten. That is why neither was
    /// ever noticed.</para>
    /// </summary>
    private static readonly Regex LineMarkerRegex = new(
        @"\[(?:THINKING|REASONING|WEB_SEARCH)\]:[^\n\r]*[\n\r]*",
        RegexOptions.Compiled);

    /// <summary>
    /// BRACKET-CLOSED markers: <c>[NAME:payload]</c>, emitted with no trailing newline. The body
    /// stays LAZY so the match ends at the payload's own closing bracket and cannot run on into
    /// the prose that follows. A payload carrying a literal <c>]</c> inside a JSON string ends
    /// the match early and leaves a fragment — which is why the two markers whose payloads are
    /// arbitrary JSON (<c>STEP</c>, <c>FILE</c>) are NOT handled here: their payloads can embed
    /// <c>]</c> freely (a DBWRITE observation literally starts with <c>[CSDL: name]</c>), and a
    /// lazy match would cut mid-payload and leak the escaped tail into the stored result. Those
    /// two go through <see cref="StripJsonPayloadMarkers"/>, which scans the JSON for its real
    /// closing bracket instead of guessing. This regex keeps only the markers whose payload is
    /// known to never contain <c>]</c> (GUIDs, agent names).
    /// </summary>
    private static readonly Regex BracketMarkerRegex = new(
        @"\[(?:Agent invoked|HITL_APPROVAL_REQUIRED|AGENT_RUN|RESEARCH_SAVED)[:\]][^\n\r]*?\][\n\r]*",
        RegexOptions.Compiled);

    /// <summary>Removes every status/step marker, leaving the model's prose.</summary>
    public static string StripMarkers(string rawContent) =>
        BracketMarkerRegex.Replace(StripJsonPayloadMarkers(LineMarkerRegex.Replace(rawContent, string.Empty)), string.Empty).Trim();

    private const string StepMarkerPrefix = "[STEP:";
    private const string FileMarkerPrefix = "[FILE:";

    /// <summary>
    /// Removes <c>[STEP:{…}]</c> and <c>[FILE:{…}]</c> markers by scanning the JSON payload for
    /// its TRUE closing bracket (string- and escape-aware), instead of matching to the first
    /// <c>]</c>.
    ///
    /// <para><b>Why this exists.</b> The orchestrator serializes a step's observation with the
    /// default JSON encoder, which escapes every non-ASCII character — and a DBWRITE observation
    /// begins with <c>[CSDL: connection name]</c>, i.e. the payload itself contains <c>]</c>. The
    /// lazy bracket regex matched only up to that embedded bracket, so the payload's escaped tail
    /// (<c>\u0110\u00E3 sao l\u01B0u…1."}]</c>) survived into <c>AgentRun.Result</c> and, from
    /// there, into the notification body the user reads.</para>
    ///
    /// <para><b>Fail-safe by construction:</b> a payload that never closes, or does not start
    /// with <c>{</c>, is left verbatim — a stripper that eats prose is the far worse failure and
    /// this repo has shipped one twice. Consuming the whole span (payload + closing <c>]</c>) is
    /// exact regardless of what the payload contains, because the scanner tracks string context
    /// and brace depth.</para>
    /// </summary>
    private static string StripJsonPayloadMarkers(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var builder = new StringBuilder(content.Length);
        var position = 0;
        while (position < content.Length)
        {
            var start = FindJsonMarker(content, position, out var prefixLength);
            if (start < 0)
            {
                builder.Append(content[position..]);
                break;
            }

            builder.Append(content[position..start]);
            var end = FindJsonPayloadEnd(content, start + prefixLength);
            if (end < 0)
            {
                // Malformed or truncated payload: keep the marker text verbatim (fail safe).
                builder.Append(content[start..(start + prefixLength)]);
                position = start + prefixLength;
                continue;
            }
            position = end; // skip past the marker's closing ']'
        }
        return builder.ToString();
    }

    /// <summary>Next <c>[STEP:</c> / <c>[FILE:</c> occurrence at or after <paramref name="start"/>, or -1.</summary>
    private static int FindJsonMarker(string content, int start, out int prefixLength)
    {
        var step = content.IndexOf(StepMarkerPrefix, start, StringComparison.Ordinal);
        var file = content.IndexOf(FileMarkerPrefix, start, StringComparison.Ordinal);
        if (step < 0 && file < 0)
        {
            prefixLength = 0;
            return -1;
        }
        if (step < 0 || (file >= 0 && file < step))
        {
            prefixLength = FileMarkerPrefix.Length;
            return file;
        }
        prefixLength = StepMarkerPrefix.Length;
        return step;
    }

    /// <summary>
    /// Index just past the closing <c>]</c> of the JSON object beginning at
    /// <paramref name="objectStart"/>, honoring strings and escapes; -1 when the payload never
    /// terminates or does not start with <c>{</c>.
    /// </summary>
    private static int FindJsonPayloadEnd(string content, int objectStart)
    {
        if (objectStart >= content.Length || content[objectStart] != '{') return -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = objectStart; i < content.Length; i++)
        {
            var c = content[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; }
            else if (c == '{') { depth++; }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return i + 1 < content.Length && content[i + 1] == ']' ? i + 2 : -1;
            }
        }
        return -1;
    }

    /// <summary>
    /// Extracts the JSON payload of every <c>[FILE:{…}]</c> marker BEFORE the content is
    /// stripped. Scheduled/agent-mode runs have no live stream consumer — the worker just
    /// drains the channel — so these descriptors are the ONLY record that the run produced
    /// downloadable files (a chart, an API response saved as a file). They are persisted on
    /// the run row (ProducedFilesJson) so the history view can render them; the marker
    /// itself is still stripped from Result, which stays clean prose.
    /// </summary>
    public static List<string> ExtractProducedFilePayloads(string rawContent)
    {
        var payloads = new List<string>();
        if (string.IsNullOrEmpty(rawContent)) return payloads;
        foreach (Match match in Regex.Matches(rawContent, @"\[FILE:(.+?)\]"))
            payloads.Add(match.Groups[1].Value);
        return payloads;
    }

    /// <summary>
    /// Extracts the web source URLs carried by <c>[WEB_SEARCH]:url|url…</c> line markers
    /// BEFORE the content is stripped, split on '|' (the orchestrator joins with '|' —
    /// see FormatWebSearchMarker; URLs cannot contain '|' because ExtractWebReferences
    /// only keeps http/https). Scheduled/agent-mode runs have no live stream consumer,
    /// so without this the run's "Đã đọc N trang web" sources would exist only in the
    /// ephemeral SSE stream. Dedupes in emission order; the marker already caps at 12.
    /// </summary>
    public static List<string> ExtractWebSourceUrls(string rawContent)
    {
        var urls = new List<string>();
        if (string.IsNullOrEmpty(rawContent)) return urls;
        foreach (Match match in Regex.Matches(rawContent, @"\[WEB_SEARCH\]:([^\n\r]+)"))
        {
            foreach (var url in match.Groups[1].Value.Split('|'))
            {
                var candidate = url.Trim();
                if (candidate.Length == 0 || urls.Contains(candidate, StringComparer.Ordinal)) continue;
                urls.Add(candidate);
            }
        }
        return urls;
    }
}
