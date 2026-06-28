using LMKit.TextGeneration;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Token management service with sliding window strategy.
/// Prevents context overflow by trimming old messages and generating summaries.
/// </summary>
public class TokenManagementService : ITokenManagementService
{
    private readonly LmModelManager _modelManager;
    private readonly ILogger<TokenManagementService> _logger;

    // H1 Fix: Vietnamese-aware chars-per-token ratios.
    // English text averages ~4.0 chars/token, Vietnamese ~2.0-2.5 due to:
    //   - Diacritics (ắ, ề, ớ) that are multi-byte → tokenizers split more aggressively
    //   - Syllable-based language → each "word" is often 1-2 tokens vs English compound words
    private const double EnglishCharsPerToken = 4.0;
    private const double VietnameseCharsPerToken = 2.2;

    // Regex to detect Vietnamese diacritic characters
    private static readonly Regex VietnamesePattern = new(
        @"[\u00C0-\u00FF\u0100-\u024F\u1E00-\u1EFF]", // Latin Extended + Vietnamese block
        RegexOptions.Compiled);
    
    // Reserve tokens for system prompt + tools + safety margin
    private const int SystemReservedTokens = 500;

    public TokenManagementService(LmModelManager modelManager, ILogger<TokenManagementService> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    /// <summary>
    /// H1 Fix: Vietnamese-aware token estimation.
    /// Detects the ratio of Vietnamese diacritics in the text and adjusts the
    /// chars-per-token ratio accordingly. More accurate than the flat 3.5 assumption.
    /// </summary>
    public int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        // Detect Vietnamese content ratio
        var vietnameseRatio = DetectVietnameseRatio(text);
        
        // Blend between English and Vietnamese ratios based on content
        var charsPerToken = EnglishCharsPerToken - (vietnameseRatio * (EnglishCharsPerToken - VietnameseCharsPerToken));
        
        return (int)Math.Ceiling(text.Length / charsPerToken);
    }

    /// <summary>
    /// Detect the ratio of Vietnamese characters (0.0 = pure English, 1.0 = pure Vietnamese).
    /// Counts diacritic and extended Latin characters as Vietnamese indicators.
    /// </summary>
    private static double DetectVietnameseRatio(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;
        
        // Count letter characters vs Vietnamese diacritic characters
        var letterCount = 0;
        var vietnameseCharCount = 0;
        
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letterCount++;
                // Vietnamese-specific Unicode ranges:
                // - Latin Extended with diacritics (common in Vietnamese)
                if (c >= '\u00C0' && c <= '\u024F' || c >= '\u1E00' && c <= '\u1EFF')
                    vietnameseCharCount++;
            }
        }
        
        if (letterCount == 0) return 0.0;
        return Math.Min((double)vietnameseCharCount / letterCount * 3.0, 1.0); // Amplify: even 33% diacritics = fully Vietnamese
    }

    public async Task<TrimmedHistoryResult> TrimHistoryAsync(List<HistoryMessage> messages, int maxTokenBudget, CancellationToken ct = default)
    {
        if (messages.Count == 0)
        {
            return new TrimmedHistoryResult { Messages = new List<HistoryMessage>(), EstimatedTokenCount = 0 };
        }

        var effectiveBudget = maxTokenBudget - SystemReservedTokens;
        if (effectiveBudget < 200) effectiveBudget = 200; // Minimum budget

        // Calculate tokens for each message
        var messageTokens = messages.Select(m => new
        {
            Message = m,
            Tokens = EstimateTokenCount(m.Content)
        }).ToList();

        var totalTokens = messageTokens.Sum(m => m.Tokens);

        // If everything fits, return as-is
        if (totalTokens <= effectiveBudget)
        {
            return new TrimmedHistoryResult
            {
                Messages = messages.ToList(),
                EstimatedTokenCount = totalTokens
            };
        }

        _logger.LogInformation(
            "✂️ Trimming history: {Total} tokens → {Budget} budget. {Count} messages",
            totalTokens, effectiveBudget, messages.Count);

        // Strategy: Keep most recent messages, summarize older ones
        // Always keep at least the last 4 messages (2 turns) for context continuity
        const int MinRecentMessages = 4;
        var recentMessages = new List<HistoryMessage>();
        var removedMessages = new List<HistoryMessage>();
        int recentTokens = 0;

        // Add messages from newest to oldest
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = messageTokens[i].Tokens;
            
            if (recentMessages.Count < MinRecentMessages || recentTokens + msgTokens <= effectiveBudget * 0.8)
            {
                recentMessages.Insert(0, messages[i]);
                recentTokens += msgTokens;
            }
            else
            {
                removedMessages.Insert(0, messages[i]);
            }
        }

        // Generate summary of removed messages
        string? summary = null;
        if (removedMessages.Count > 0)
        {
            summary = GenerateQuickSummary(removedMessages);
            
            // Inject summary as a system-like message at the start
            var summaryMessage = new HistoryMessage
            {
                Role = "system",
                Content = $"[Tóm tắt cuộc trò chuyện trước đó ({removedMessages.Count} tin nhắn)]: {summary}",
                CreatedAt = removedMessages.First().CreatedAt
            };

            recentMessages.Insert(0, summaryMessage);
            recentTokens += EstimateTokenCount(summaryMessage.Content);
        }

        _logger.LogInformation(
            "✂️ Trimmed: kept {Kept} messages ({Tokens} tokens), removed {Removed}, summary: {HasSummary}",
            recentMessages.Count, recentTokens, removedMessages.Count, summary != null);

        return new TrimmedHistoryResult
        {
            Messages = recentMessages,
            ConversationSummary = summary,
            RemovedMessageCount = removedMessages.Count,
            EstimatedTokenCount = recentTokens
        };
    }

    /// <summary>
    /// Generate a quick extractive summary without calling the LLM.
    /// Takes the first sentence of each message to create a compressed timeline.
    /// </summary>
    private string GenerateQuickSummary(List<HistoryMessage> messages)
    {
        var summaryParts = new List<string>();
        
        foreach (var msg in messages)
        {
            var content = msg.Content.Trim();
            // Take first sentence or first 100 chars
            var firstSentence = content.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? content;
            
            if (firstSentence.Length > 100)
                firstSentence = firstSentence.Substring(0, 100) + "...";
            
            var roleLabel = msg.Role == "user" ? "Người dùng" : "AI";
            summaryParts.Add($"{roleLabel}: {firstSentence}");
        }

        // Limit summary to ~300 chars
        var summary = string.Join(" → ", summaryParts);
        if (summary.Length > 500)
            summary = summary.Substring(0, 500) + "...";

        return summary;
    }
}
