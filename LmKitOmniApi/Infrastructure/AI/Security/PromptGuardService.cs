using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Multi-layer prompt injection detection service.
/// Uses pattern matching + heuristic analysis to detect:
/// - Direct prompt injection (user trying to override system prompt)
/// - Indirect prompt injection (injected via external data)
/// - Jailbreak attempts
/// - Data exfiltration attempts
/// - Tool abuse patterns
/// </summary>
public class PromptGuardService : IPromptGuardService
{
    private readonly ILogger<PromptGuardService> _logger;

    // Regex patterns for common prompt injection techniques
    private static readonly List<(string Pattern, string ThreatType, string Description, double Weight)> InjectionPatterns = new()
    {
        // Direct injection - Override system instructions
        (@"(?i)(ignore|disregard|forget|override|bypass)\s+(all\s+)?(previous|above|prior|earlier|system)\s+(instructions?|prompts?|rules?|guidelines?|constraints?)", 
            "PromptInjection", "Attempt to override system instructions", 0.9),
        
        (@"(?i)you\s+are\s+now\s+(a|an|the)\s+", 
            "Jailbreak", "Role reassignment attempt", 0.7),
        
        (@"(?i)(pretend|act\s+as\s+if|imagine|roleplay|simulate)\s+(you\s+are|that\s+you|being)\s+",
            "Jailbreak", "Role-playing jailbreak attempt", 0.7),
        
        (@"(?i)do\s+not\s+follow\s+(your|the|any)\s+(safety|content|ethical)\s+(guidelines?|policies?|rules?)",
            "Jailbreak", "Safety bypass attempt", 0.95),
        
        // System prompt extraction
        (@"(?i)(show|reveal|display|print|output|repeat|echo)\s+(your|the|system)\s+(system\s+)?(prompt|instructions?|rules?|guidelines?)",
            "DataExfiltration", "System prompt extraction attempt", 0.85),
        
        (@"(?i)what\s+(are|were)\s+your\s+(initial|original|system|first)\s+(instructions?|prompts?|rules?)",
            "DataExfiltration", "System prompt probing", 0.8),
        
        // Token smuggling / delimiter injection
        (@"(?i)(\[\/INST\]|\<\/s\>|\<\|im_end\|\>|\<\|endoftext\|\>|<\|system\|>|\[SYSTEM\])",
            "PromptInjection", "Token/delimiter injection attempt", 0.95),
        
        // Tool abuse patterns
        (@"(?i)(execute|run|call|invoke)\s+(any|all|every|arbitrary)\s+(command|function|tool|code|script)",
            "ToolAbuse", "Unrestricted tool execution attempt", 0.85),
        
        (@"(?i)(delete|drop|truncate|destroy|remove)\s+(all|every|the)\s+(data|database|table|file|record)",
            "ToolAbuse", "Destructive operation attempt", 0.9),
        
        // Indirect injection markers
        (@"(?i)(IMPORTANT|URGENT|CRITICAL|OVERRIDE):\s*(ignore|disregard|new\s+instructions?)",
            "PromptInjection", "Indirect injection via emphasis markers", 0.8),
        
        // Data exfiltration
        (@"(?i)(send|post|upload|transmit|forward)\s+(to|via)\s+(http|https|ftp|email|webhook)",
            "DataExfiltration", "Data exfiltration via external service", 0.75),
        
        // Encoding bypass attempts
        (@"(?i)(base64|hex|rot13|binary|unicode|url)\s*(encode|decode|convert)",
            "PromptInjection", "Encoding-based bypass attempt", 0.6),
        
        // Memory/context poisoning
        (@"(?i)(remember|memorize|store|save)\s+(that|this|the\s+following)\s+.*(always|forever|permanently)",
            "ContextInjection", "Persistent memory poisoning attempt", 0.7),
    };

    // Heuristic thresholds
    private const int SuspiciousSpecialCharThreshold = 15;
    private const double MaxAllowedRiskScore = 0.7;

    public PromptGuardService(ILogger<PromptGuardService> logger)
    {
        _logger = logger;
    }

    public Task<PromptGuardResult> AnalyzeInputAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult(PromptGuardResult.Safe());

        var detections = new List<PromptThreatDetection>();
        double maxRisk = 0.0;

        // Layer 1: Pattern matching
        foreach (var (pattern, threatType, description, weight) in InjectionPatterns)
        {
            var matches = Regex.Matches(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
            if (matches.Count > 0)
            {
                var detection = new PromptThreatDetection
                {
                    ThreatType = threatType,
                    Description = description,
                    Confidence = weight,
                    MatchedPattern = matches[0].Value.Length > 100 
                        ? matches[0].Value.Substring(0, 100) + "..." 
                        : matches[0].Value
                };
                detections.Add(detection);
                maxRisk = Math.Max(maxRisk, weight);
            }
        }

        // Layer 2: Heuristic analysis
        var heuristicDetections = AnalyzeHeuristics(input);
        detections.AddRange(heuristicDetections);
        foreach (var d in heuristicDetections)
        {
            maxRisk = Math.Max(maxRisk, d.Confidence);
        }

        // Layer 3: Structure analysis (nested instructions, unusual formatting)
        var structureDetections = AnalyzeStructure(input);
        detections.AddRange(structureDetections);
        foreach (var d in structureDetections)
        {
            maxRisk = Math.Max(maxRisk, d.Confidence);
        }

        if (detections.Count == 0)
            return Task.FromResult(PromptGuardResult.Safe());

        // Calculate composite risk
        var compositeRisk = CalculateCompositeRisk(detections);
        var riskLevel = compositeRisk switch
        {
            >= 0.9 => "Critical",
            >= 0.7 => "High",
            >= 0.5 => "Medium",
            >= 0.3 => "Low",
            _ => "None"
        };

        var isSafe = compositeRisk < MaxAllowedRiskScore;

        if (!isSafe)
        {
            _logger.LogWarning("🛡️ Prompt injection detected! Risk: {Risk:P0} Level: {Level}. Detections: {Count}",
                compositeRisk, riskLevel, detections.Count);
        }

        var result = new PromptGuardResult
        {
            IsSafe = isSafe,
            RiskScore = compositeRisk,
            RiskLevel = riskLevel,
            Detections = detections
        };

        return Task.FromResult(result);
    }

    public Task<PromptGuardResult> AnalyzeOutputAsync(string output, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Task.FromResult(PromptGuardResult.Safe());

        var detections = new List<PromptThreatDetection>();

        // Check for system prompt leakage in output
        var leakagePatterns = new[]
        {
            (@"(?i)(system\s+prompt|my\s+instructions?\s+are|i\s+was\s+told\s+to|my\s+guidelines?\s+(say|are))", "SystemPromptLeakage"),
            (@"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN)\s*[:=]\s*\S+", "CredentialLeakage"),
            (@"\b(?:\d{3}[-.\s]?\d{2}[-.\s]?\d{4})\b", "PIILeakage"), // SSN pattern
            (@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", "PIILeakage"), // Email pattern
        };

        foreach (var (pattern, threatType) in leakagePatterns)
        {
            var matches = Regex.Matches(output, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
            if (matches.Count > 0)
            {
                detections.Add(new PromptThreatDetection
                {
                    ThreatType = threatType,
                    Description = $"Potential {threatType} detected in output",
                    Confidence = 0.7,
                    MatchedPattern = "[REDACTED]"
                });
            }
        }

        if (detections.Count == 0)
            return Task.FromResult(PromptGuardResult.Safe());

        var risk = CalculateCompositeRisk(detections);
        return Task.FromResult(new PromptGuardResult
        {
            IsSafe = risk < MaxAllowedRiskScore,
            RiskScore = risk,
            RiskLevel = risk >= 0.7 ? "High" : "Medium",
            Detections = detections
        });
    }

    private List<PromptThreatDetection> AnalyzeHeuristics(string input)
    {
        var detections = new List<PromptThreatDetection>();

        // Check for excessive special characters (possible encoding bypass)
        var specialCharCount = input.Count(c => c == '{' || c == '}' || c == '<' || c == '>' || c == '|' || c == '\\');
        if (specialCharCount > SuspiciousSpecialCharThreshold)
        {
            detections.Add(new PromptThreatDetection
            {
                ThreatType = "PromptInjection",
                Description = $"Excessive special characters ({specialCharCount}) may indicate encoding attack",
                Confidence = 0.5
            });
        }

        // Check for very long inputs (potential context overflow attack)
        if (input.Length > 10000)
        {
            detections.Add(new PromptThreatDetection
            {
                ThreatType = "ContextInjection",
                Description = $"Unusually long input ({input.Length} chars) may be context overflow attempt",
                Confidence = 0.4
            });
        }

        // Check for multiple language mixing (obfuscation technique)
        var hasLatin = Regex.IsMatch(input, @"[a-zA-Z]{3,}");
        var hasCyrillic = Regex.IsMatch(input, @"[\u0400-\u04FF]{3,}");
        var hasArabic = Regex.IsMatch(input, @"[\u0600-\u06FF]{3,}");
        var scriptCount = (hasLatin ? 1 : 0) + (hasCyrillic ? 1 : 0) + (hasArabic ? 1 : 0);
        
        if (scriptCount > 1)
        {
            detections.Add(new PromptThreatDetection
            {
                ThreatType = "PromptInjection",
                Description = "Mixed scripts detected — possible obfuscation technique",
                Confidence = 0.4
            });
        }

        return detections;
    }

    private List<PromptThreatDetection> AnalyzeStructure(string input)
    {
        var detections = new List<PromptThreatDetection>();

        // Check for nested instruction blocks (common injection technique)
        var instructionBlockCount = Regex.Matches(input, @"(?i)(###|---|\*\*\*|===)\s*(system|instruction|rule|important)", 
            RegexOptions.None, TimeSpan.FromMilliseconds(200)).Count;
        
        if (instructionBlockCount > 0)
        {
            detections.Add(new PromptThreatDetection
            {
                ThreatType = "PromptInjection",
                Description = $"Found {instructionBlockCount} instruction block marker(s) in user input",
                Confidence = 0.75
            });
        }

        // Check for markdown/HTML that could hide instructions
        var hiddenContentPatterns = new[]
        {
            @"<!--.*?-->", // HTML comments
            @"\[//\]:\s*#\s*\(.*?\)", // Markdown hidden comments
        };

        foreach (var pattern in hiddenContentPatterns)
        {
            if (Regex.IsMatch(input, pattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(200)))
            {
                detections.Add(new PromptThreatDetection
                {
                    ThreatType = "PromptInjection",
                    Description = "Hidden content detected (HTML/Markdown comments with potential instructions)",
                    Confidence = 0.7
                });
            }
        }

        return detections;
    }

    private double CalculateCompositeRisk(List<PromptThreatDetection> detections)
    {
        if (detections.Count == 0) return 0.0;
        
        // Use max confidence as base, with diminishing additions for multiple detections
        var ordered = detections.OrderByDescending(d => d.Confidence).ToList();
        double composite = ordered[0].Confidence;
        
        for (int i = 1; i < ordered.Count; i++)
        {
            // Each additional detection adds a diminishing contribution
            composite += ordered[i].Confidence * (1.0 - composite) * 0.3;
        }
        
        return Math.Min(composite, 1.0);
    }
}
