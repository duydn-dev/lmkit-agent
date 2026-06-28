namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Service for detecting prompt injection and other adversarial inputs.
/// Addresses OWASP Top 10 for LLM: Prompt Injection (LLM01).
/// </summary>
public interface IPromptGuardService
{
    /// <summary>Analyze input for prompt injection attempts.</summary>
    Task<PromptGuardResult> AnalyzeInputAsync(string input, CancellationToken ct = default);
    
    /// <summary>Analyze output for data leakage or policy violations.</summary>
    Task<PromptGuardResult> AnalyzeOutputAsync(string output, CancellationToken ct = default);
}

public class PromptGuardResult
{
    public bool IsSafe { get; set; } = true;
    public double RiskScore { get; set; }
    public string RiskLevel { get; set; } = "None"; // None, Low, Medium, High, Critical
    public List<PromptThreatDetection> Detections { get; set; } = new();
    
    public static PromptGuardResult Safe() => new() { IsSafe = true, RiskScore = 0.0 };
    public static PromptGuardResult Unsafe(double score, string level, List<PromptThreatDetection> detections) 
        => new() { IsSafe = false, RiskScore = score, RiskLevel = level, Detections = detections };
}

public class PromptThreatDetection
{
    public string ThreatType { get; set; } = string.Empty; // "PromptInjection", "Jailbreak", "DataExfiltration", "ToolAbuse"
    public string Description { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? MatchedPattern { get; set; }
}
