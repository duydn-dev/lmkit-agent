using MediatR;

namespace LmKitOmniApi.Application.Audit.Queries;

/// <summary>
/// Lists audit-log entries for a tenant, newest first, with optional filters and
/// paging. Always tenant-scoped by the controller from the authenticated
/// principal so one tenant can never read another tenant's activity.
/// </summary>
public sealed class GetAuditLogsQuery : IRequest<AuditLogPageDto>
{
    public Guid TenantId { get; set; }

    /// <summary>Optional exact-match filter on <c>ActorType</c> (e.g. "agent", "user", "system").</summary>
    public string? ActorType { get; set; }

    /// <summary>Optional exact-match filter on <c>Action</c> (e.g. "AI.Tool.Invoke").</summary>
    public string? Action { get; set; }

    /// <summary>Optional case-insensitive contains filter on <c>EntityType</c> (the tool name for agent invocations).</summary>
    public string? EntityType { get; set; }

    /// <summary>Optional inclusive lower bound on <c>CreatedAtUtc</c>.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Optional inclusive upper bound on <c>CreatedAtUtc</c>.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>1-based page number. Values below 1 are clamped to 1 by the handler.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size, clamped by the handler to the range [1, 100].</summary>
    public int PageSize { get; set; } = 25;
}

/// <summary>A single audit-log row, flattened for the admin activity view.</summary>
public sealed class AuditLogDto
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One page of audit rows plus the total count for pagination controls.</summary>
public sealed class AuditLogPageDto
{
    public List<AuditLogDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
