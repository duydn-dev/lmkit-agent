using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Agent Skill Registry — auto-discovers and catalogs all available tools,
/// specialized agents, and MCP external tools. Provides a unified directory
/// that the ReAct reasoning step uses to decide which action to take.
/// 
/// Addresses gap: "agent-skills — Không có Skill Registry/Discovery"
/// </summary>
public class AgentSkillRegistry
{
    private readonly IEnumerable<ISpecializedAgent> _agents;
    private readonly McpClientService _mcpClient;
    private readonly ILogger<AgentSkillRegistry> _logger;

    // Built-in tools (hardcoded, always available)
    private static readonly List<SkillEntry> BuiltInSkills = new()
    {
        new("RAG", "Tìm kiếm kho tri thức nội bộ (knowledge base)", "builtin", new[] { "search", "knowledge", "tìm", "tra cứu", "tài liệu" }),
        new("VISION", "Phân tích hình ảnh, OCR, nhận dạng nội dung", "builtin", new[] { "ảnh", "image", "ocr", "hình", "photo" }),
        new("SPEECH", "Chuyển đổi giọng nói thành văn bản", "builtin", new[] { "audio", "giọng", "voice", "wav", "mp3" }),
        new("NLP", "Phân tích văn bản: sentiment, NER, PII", "builtin", new[] { "phân tích", "sentiment", "entity", "cảm xúc" }),
        new("WEB_SEARCH", "Tìm kiếm thông tin trên internet", "builtin", new[] { "web", "google", "search", "internet", "tìm trên mạng" }),
        new("DELEGATE", "Ủy quyền cho agent chuyên biệt (Research/Analysis/Vision)", "builtin", new[] { "nghiên cứu", "research", "chuyên sâu", "delegate" }),
        new("MCP", "Gọi công cụ từ MCP server bên ngoài", "builtin", new[] { "external", "mcp", "bên ngoài", "tool" }),
        new("SUMMARIZE", "Tóm tắt văn bản/tài liệu dài", "builtin", new[] { "tóm tắt", "summarize", "summary", "rút gọn" }),
    };

    public AgentSkillRegistry(
        IEnumerable<ISpecializedAgent> agents,
        McpClientService mcpClient,
        ILogger<AgentSkillRegistry> logger)
    {
        _agents = agents;
        _mcpClient = mcpClient;
        _logger = logger;
    }

    /// <summary>
    /// Get the full skill directory for injection into ReAct reasoning prompt.
    /// Includes built-in tools, specialized agents, and discovered MCP tools.
    /// </summary>
    public async Task<string> GetSkillDirectoryAsync(CancellationToken ct = default)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Available actions:");
        builder.AppendLine();

        // 1. Built-in tools
        foreach (var skill in BuiltInSkills)
        {
            builder.AppendLine($"- {skill.Name}: {skill.Description}");
        }

        // 2. Specialized agents
        foreach (var agent in _agents)
        {
            builder.AppendLine($"- DELEGATE→{agent.AgentName}: {agent.Description}");
        }

        // 3. MCP tools (dynamic discovery, cached)
        try
        {
            var mcpTools = await _mcpClient.DiscoverToolsAsync(ct);
            foreach (var tool in mcpTools)
            {
                builder.AppendLine($"- MCP→{tool.Name}: {tool.Description} (Server: {tool.ServerName})");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ MCP tool discovery failed: {Error}", ex.Message);
        }

        builder.AppendLine();
        builder.AppendLine("- DONE: Không cần thêm hành động, tiến hành trả lời");

        return builder.ToString();
    }

    /// <summary>
    /// Get just the list of valid action names for parsing.
    /// </summary>
    public List<string> GetValidActionNames()
    {
        var actions = BuiltInSkills.Select(s => s.Name).ToList();
        actions.Add("DONE");
        return actions;
    }
}

public record SkillEntry(string Name, string Description, string Source, string[] Keywords);
