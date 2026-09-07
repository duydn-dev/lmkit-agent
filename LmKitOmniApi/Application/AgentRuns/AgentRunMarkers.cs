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

    private static readonly Regex MarkerRegex = new(
        @"\[(?:THINKING|REASONING|WEB_SEARCH|Agent invoked|STEP|FILE|HITL_APPROVAL_REQUIRED|AGENT_RUN|RESEARCH_SAVED)[:\]][^\n\r]*?(?:\][\n\r]*|(?=\[)|$)",
        RegexOptions.Compiled);

    /// <summary>Removes every status/step marker, leaving the model's prose.</summary>
    public static string StripMarkers(string rawContent) => MarkerRegex.Replace(rawContent, string.Empty).Trim();
}
