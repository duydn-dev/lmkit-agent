using MediatR;

namespace LmKitOmniApi.Application.Memory.Queries;

/// <summary>Lists the caller's non-expired agent memories, newest first.</summary>
public class ListAgentMemoriesQuery : IRequest<List<AgentMemoryListItemDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Projection returned by <see cref="ListAgentMemoriesQuery"/>. Property names
/// and declaration order intentionally mirror the previous anonymous-type
/// projection so the serialized JSON shape is unchanged.
/// </summary>
public class AgentMemoryListItemDto
{
    public Guid Id { get; set; }
    public string MemoryType { get; set; } = string.Empty;
    public string MemoryKey { get; set; } = string.Empty;
    public string MemoryValue { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
