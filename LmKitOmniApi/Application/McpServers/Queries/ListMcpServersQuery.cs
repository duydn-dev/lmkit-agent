using MediatR;

namespace LmKitOmniApi.Application.McpServers.Queries;

public class ListMcpServersQuery : IRequest<List<McpServerSummaryDto>>
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// Mirrors the anonymous projection previously built inline in McpServersController.List.
/// Property names and declaration order are load-bearing for wire-identical JSON.
/// Headers are never returned — only whether any exist.
/// </summary>
public sealed class McpServerSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool TrustReadOnlyAnnotations { get; set; }
    public bool HasHeaders { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
