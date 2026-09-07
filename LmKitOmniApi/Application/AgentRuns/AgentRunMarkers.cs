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
    /// the match early and leaves a fragment — accepted deliberately, because the alternative is
    /// a greedy match that can swallow the answer, and this repo has twice shipped a stripper
    /// that did exactly that.
    /// </summary>
    private static readonly Regex BracketMarkerRegex = new(
        @"\[(?:Agent invoked|STEP|FILE|HITL_APPROVAL_REQUIRED|AGENT_RUN|RESEARCH_SAVED)[:\]][^\n\r]*?\][\n\r]*",
        RegexOptions.Compiled);

    /// <summary>Removes every status/step marker, leaving the model's prose.</summary>
    public static string StripMarkers(string rawContent) =>
        BracketMarkerRegex.Replace(LineMarkerRegex.Replace(rawContent, string.Empty), string.Empty).Trim();
}
