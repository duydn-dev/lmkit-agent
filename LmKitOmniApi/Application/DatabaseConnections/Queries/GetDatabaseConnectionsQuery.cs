using MediatR;

namespace LmKitOmniApi.Application.DatabaseConnections.Queries;

/// <summary>Lists the tenant's database connections (never the connection string), newest first.</summary>
public sealed class GetDatabaseConnectionsQuery : IRequest<List<DatabaseConnectionDto>>
{
    public Guid TenantId { get; set; }
}

public sealed class DatabaseConnectionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool AllowWrites { get; set; }
    public bool IsIndexed { get; set; }
    public string IndexStatus { get; set; } = string.Empty;
    public string? LastIndexError { get; set; }
    public DateTime? LastIndexedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
