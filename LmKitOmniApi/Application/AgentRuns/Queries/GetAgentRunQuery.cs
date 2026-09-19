using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Queries;

/// <summary>Returns one agent run with its ordered steps, scoped to the caller.</summary>
public sealed class GetAgentRunQuery : IRequest<AgentRunDetailDto?>
{
    public Guid RunId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class AgentRunDetailDto
{
    public Guid Id { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public List<AgentRunStepDto> Steps { get; set; } = new();

    /// <summary>File descriptors the run's tools produced (same shape as the live
    /// stream's [FILE:] payloads) so the history view can render downloads.</summary>
    public List<ProducedFileDto> ProducedFiles { get; set; } = new();

    /// <summary>Web source URLs the run consulted ([WEB_SEARCH] payloads, persisted for
    /// scheduled runs) so the history view can render the "Đã đọc N trang web" chip
    /// and its source drawer, exactly like a live chat answer.</summary>
    public List<string> WebSources { get; set; } = new();
}

public sealed class ProducedFileDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Field name khớp marker [FILE:] ("size") — FE/mobile đã parse theo đó.</summary>
    public long Size { get; set; }
}

public sealed class AgentRunStepDto
{
    public int Ordinal { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
