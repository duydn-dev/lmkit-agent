namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Canonical threat-type identifiers shared by the output guardrail detector
/// (<c>PromptGuardService.AnalyzeOutputAsync</c>), the full-text redaction pass
/// (<c>OutputGuardrailFilter.RedactForDetectedThreats</c>) and the streaming
/// holdback gate in <c>AgentOrchestrator</c>. Those three sites compare these
/// values against one another, so a divergent literal would silently disable a
/// redaction latch. The string values are load-bearing and must not change.
/// </summary>
public static class ThreatTypes
{
    public const string CredentialLeakage = "CredentialLeakage";
    public const string PIILeakage = "PIILeakage";
    public const string SystemPromptLeakage = "SystemPromptLeakage";
}
