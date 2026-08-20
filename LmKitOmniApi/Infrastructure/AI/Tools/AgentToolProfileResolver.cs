using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

[Flags]
public enum AgentToolProfile
{
    SafeChat = 1,
    Research = 2,
    ImageRead = 4,
    AudioRead = 8,
    ExternalMcp = 16
}

/// <summary>Selects the smallest capability set needed for a request.</summary>
public static partial class AgentToolProfileResolver
{
    public static AgentToolProfile Resolve(string query)
    {
        query ??= string.Empty;
        var profile = AgentToolProfile.SafeChat;
        if (ResearchPattern().IsMatch(query)) profile |= AgentToolProfile.Research;
        if (ImagePattern().IsMatch(query)) profile |= AgentToolProfile.ImageRead;
        if (AudioPattern().IsMatch(query)) profile |= AgentToolProfile.AudioRead;
        if (McpPattern().IsMatch(query)) profile |= AgentToolProfile.ExternalMcp;
        return profile;
    }

    [GeneratedRegex(@"\b(web|internet|online|latest|current|mới nhất|hiện tại)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResearchPattern();

    [GeneratedRegex("\\.(jpg|jpeg|png|bmp|webp)(?:\\s|\\\"|'|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    [GeneratedRegex("\\.(wav|mp3|flac)(?:\\s|\\\"|'|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AudioPattern();

    [GeneratedRegex(@"\b(mcp|external tool|integration)\b", RegexOptions.IgnoreCase)]
    private static partial Regex McpPattern();
}
