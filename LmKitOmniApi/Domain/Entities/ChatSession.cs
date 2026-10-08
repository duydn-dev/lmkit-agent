using System.ComponentModel.DataAnnotations;

namespace LmKitOmniApi.Domain.Entities;

public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Title { get; set; } = "New Chat";
    public string? Summary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Optional custom agent this session chats with (persona + tool/knowledge scope).</summary>
    public Guid? CustomAgentId { get; set; }

    /// <summary>Optional project this session belongs to (shared instructions + grouping).</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// True when this session is the hidden substrate of an <see cref="AgentRun"/>.
    /// Such sessions are excluded from the chat history list/search so agent runs
    /// never appear as ordinary conversations.
    /// </summary>
    public bool IsAgentRun { get; set; }

    /// <summary>Owning schedule for a scheduled AgentRun; null for manual runs.</summary>
    public Guid? ScheduledTaskId { get; set; }

    /// <summary>
    /// True for a temporary ("Chat tạm thời") conversation — ChatGPT/Gemini style.
    /// The turns stream normally but no <see cref="ChatMessage"/> rows are persisted,
    /// and the session is excluded from the chat history list/search exactly like
    /// <see cref="IsAgentRun"/>, so a temporary chat never appears as a saved
    /// conversation. Defaults to false, so existing sessions stay ordinary.
    /// </summary>
    public bool IsEphemeral { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
    public CustomAgent? CustomAgent { get; set; }
    public Project? Project { get; set; }
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatSessionId { get; set; }
    public string Role { get; set; } = "user"; // "system", "user", "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Token của lượt GỬI LÊN model (lịch sử đã cắt + câu hỏi), và token model sinh ra.
    /// Ghi lúc lưu row assistant — CHỈ trên row assistant, vì đây là chi phí của một lần
    /// suy luận; row user luôn để 0 để mọi tổng hợp theo token không đếm hai lần cùng một lượt.
    /// Là ƯỚC LƯỢNG (<c>ITokenManagementService.EstimateTokenCount</c>), không phải usage
    /// do model trả về — LM-Kit chạy in-process nên không có object usage như API HTTP.
    /// </summary>
    public int PromptTokens { get; set; }

    /// <summary>Token model đã sinh cho lượt này (cũng là ước lượng). 0 trên row user.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Model id đã phục vụ lượt này (vd. "gemma4:e4b"); null trên row user.</summary>
    [MaxLength(100)]
    public string? ModelName { get; set; }

    /// <summary>Thời gian từ lúc bắt đầu gọi model tới khi stream xong (ms). 0 = không đo được.</summary>
    public int LatencyMs { get; set; }

    public ChatSession? ChatSession { get; set; }
}
