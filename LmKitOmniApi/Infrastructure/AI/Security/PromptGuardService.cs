using System.Globalization;
using System.Text;
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
    private static readonly (Regex Rx, string ThreatType, string Description, double Weight)[] InjectionPatterns =
    {
        // Direct injection - Override system instructions
        (new Regex(@"(?i)(ignore|disregard|forget|override|bypass)\s+(all\s+)?(previous|above|prior|earlier|system)\s+(instructions?|prompts?|rules?|guidelines?|constraints?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), 
            "PromptInjection", "Attempt to override system instructions", 0.9),
        
        (new Regex(@"(?i)you\s+are\s+now\s+(a|an|the)\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), 
            "Jailbreak", "Role reassignment attempt", 0.7),
        
        (new Regex(@"(?i)(pretend|act\s+as\s+if|imagine|roleplay|simulate)\s+(you\s+are|that\s+you|being)\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Role-playing jailbreak attempt", 0.7),
        
        (new Regex(@"(?i)do\s+not\s+follow\s+(your|the|any)\s+(safety|content|ethical)\s+(guidelines?|policies?|rules?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Safety bypass attempt", 0.95),
        
        // System prompt extraction
        (new Regex(@"(?i)(show|reveal|display|print|output|repeat|echo)\s+(your|the|system)\s+(system\s+)?(prompt|instructions?|rules?|guidelines?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "DataExfiltration", "System prompt extraction attempt", 0.85),
        
        (new Regex(@"(?i)what\s+(are|were)\s+your\s+(initial|original|system|first)\s+(instructions?|prompts?|rules?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "DataExfiltration", "System prompt probing", 0.8),
        
        // Token smuggling / delimiter injection
        (new Regex(@"(?i)(\[\/INST\]|\<\/s\>|\<\|im_end\|\>|\<\|endoftext\|\>|<\|system\|>|\[SYSTEM\])", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "PromptInjection", "Token/delimiter injection attempt", 0.95),
        
        // Tool abuse patterns
        (new Regex(@"(?i)(execute|run|call|invoke)\s+(any|all|every|arbitrary)\s+(command|function|tool|code|script)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "ToolAbuse", "Unrestricted tool execution attempt", 0.85),
        
        (new Regex(@"(?i)(delete|drop|truncate|destroy|remove)\s+(all|every|the)\s+(data|database|table|file|record)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "ToolAbuse", "Destructive operation attempt", 0.9),
        
        // Indirect injection markers
        (new Regex(@"(?i)(IMPORTANT|URGENT|CRITICAL|OVERRIDE):\s*(ignore|disregard|new\s+instructions?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "PromptInjection", "Indirect injection via emphasis markers", 0.8),
        
        // Data exfiltration
        (new Regex(@"(?i)(send|post|upload|transmit|forward)\s+(to|via)\s+(http|https|ftp|email|webhook)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "DataExfiltration", "Data exfiltration via external service", 0.75),
        
        // Encoding bypass attempts
        (new Regex(@"(?i)(base64|hex|rot13|binary|unicode|url)\s*(encode|decode|convert)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "PromptInjection", "Encoding-based bypass attempt", 0.6),
        
        // Memory/context poisoning
        (new Regex(@"(?i)(remember|memorize|store|save)\s+(that|this|the\s+following)\s+.*(always|forever|permanently)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "ContextInjection", "Persistent memory poisoning attempt", 0.7),
    };

    // Vietnamese-language prompt injection / jailbreak patterns.
    // Matched against a diacritic-folded copy of the input (see RemoveDiacritics), so both
    // accented ("bỏ qua") and unaccented ("bo qua") spellings are detected. Patterns are
    // therefore written in folded ASCII (no diacritics, 'đ' → 'd') and are case-insensitive.
    private static readonly (Regex Rx, string ThreatType, string Description, double Weight)[] VietnameseInjectionPatterns =
    {
        // Override / ignore prior instructions — e.g. "bỏ qua tất cả hướng dẫn phía trên"
        (new Regex(@"(?i)\b(bo qua|phot lo|lo di|quen)\s+(di\s+)?(tat ca|toan bo|het|moi|cac|nhung)?\s*(huong dan|chi dan|chi thi|quy tac|quy dinh|nguyen tac|lenh|yeu cau|loi nhac|system prompt|prompt)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "PromptInjection", "Vietnamese attempt to override system instructions", 0.9),

        // Role reassignment — "bạn (giờ|bây giờ) là ..."
        (new Regex(@"(?i)\bban\s+(bay gio|gio day|hien gio|hien tai|gio)\s+la\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Vietnamese role reassignment attempt", 0.7),

        // Roleplay — "đóng vai ..." (negative lookahead excludes the benign "đóng vai trò")
        (new Regex(@"(?i)\bdong vai(?!\s+tro\b)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Vietnamese role-playing jailbreak attempt", 0.7),

        // Pretend / impersonate — "giả vờ (là|rằng) ..."
        (new Regex(@"(?i)\bgia vo\b(\s+(la|rang|nhu))?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Vietnamese pretend/impersonation jailbreak attempt", 0.7),

        // Developer / unrestricted mode — "chế độ nhà phát triển"
        (new Regex(@"(?i)\bche do\s+((nha\s+)?phat trien|dan|khong gioi han|tu do|khong kiem duyet|khong an toan)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Vietnamese developer/unrestricted mode jailbreak", 0.85),

        // Refuse to comply — "không tuân theo (quy tắc|an toàn ...)"
        (new Regex(@"(?i)\bkhong\s+(tuan theo|tuan thu|lam theo|nghe theo|can tuan|phai tuan)\s+(cac|moi|nhung|bat ky|theo)?\s*(quy tac|quy dinh|huong dan|chi dan|nguyen tac|chinh sach|gioi han|rang buoc|an toan|kiem duyet)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "Jailbreak", "Vietnamese safety-bypass / non-compliance attempt", 0.9),

        // System prompt / instruction extraction — "tiết lộ prompt", "in ra hướng dẫn hệ thống"
        (new Regex(@"(?i)\b(tiet lo|hien thi|in ra|cho\s+\w+\s+xem|cho xem|doc|lap lai|nhac lai|liet ke)\s+(cho\s+\w+\s+)?(noi dung\s+)?(cac\s+|nhung\s+)?(system\s+|he thong\s+)?(system prompt|prompt|huong dan he thong|chi dan he thong|lenh he thong|cau lenh he thong|huong dan goc|huong dan ban dau|loi nhac he thong)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "DataExfiltration", "Vietnamese system prompt extraction attempt", 0.85),

        // System prompt probing — "hướng dẫn (ban đầu|gốc|hệ thống) của bạn là gì"
        (new Regex(@"(?i)\b(huong dan|chi dan|lenh|quy tac|prompt|system prompt)\s+(goc|ban dau|dau tien|he thong|khoi tao)\s+(cua ban|cua may|cua he thong|cua ai)?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)),
            "DataExfiltration", "Vietnamese system prompt probing", 0.8),
    };

    // Output leakage patterns — precompiled once at load; scanned in order against model output.
    private static readonly (Regex Rx, string ThreatType)[] LeakagePatterns =
    {
        (new Regex(@"(?i)(system\s+prompt|my\s+instructions?\s+are|i\s+was\s+told\s+to|my\s+guidelines?\s+(say|are))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), ThreatTypes.SystemPromptLeakage),
        (new Regex(@"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN)\s*[:=]\s*\S+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), ThreatTypes.CredentialLeakage),
        (new Regex(@"(?i)\bBEARER\s+\S+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), ThreatTypes.CredentialLeakage),
        (new Regex(@"\b(?:\d{3}[-.\s]?\d{2}[-.\s]?\d{4})\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), ThreatTypes.PIILeakage), // SSN pattern
        (new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)), ThreatTypes.PIILeakage), // Email pattern
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
        foreach (var (rx, threatType, description, weight) in InjectionPatterns)
        {
            var matches = rx.Matches(input);
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

        // Layer 1b: Vietnamese pattern matching (diacritic-insensitive).
        // Vietnamese users routinely type with or without accents, so match against a
        // folded copy of the input where diacritics and 'đ' are normalized to base ASCII.
        var foldedInput = RemoveDiacritics(input);
        foreach (var (rx, threatType, description, weight) in VietnameseInjectionPatterns)
        {
            var matches = rx.Matches(foldedInput);
            if (matches.Count > 0)
            {
                detections.Add(new PromptThreatDetection
                {
                    ThreatType = threatType,
                    Description = description,
                    Confidence = weight,
                    MatchedPattern = matches[0].Value.Length > 100
                        ? matches[0].Value.Substring(0, 100) + "..."
                        : matches[0].Value
                });
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

        // Check for system prompt leakage in output (patterns precompiled in LeakagePatterns).
        foreach (var (rx, threatType) in LeakagePatterns)
        {
            var matches = rx.Matches(output);
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

    /// <summary>
    /// Fold diacritics to base ASCII letters so pattern matching tolerates accented and
    /// unaccented spellings alike. Also maps the Vietnamese letter 'đ'/'Đ' (which Unicode
    /// does not decompose via normalization) to 'd'/'D'.
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
