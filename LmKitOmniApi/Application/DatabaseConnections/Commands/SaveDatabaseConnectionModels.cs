using MediatR;

namespace LmKitOmniApi.Application.DatabaseConnections.Commands;

/// <summary>Admin payload for creating/updating an external DB connection. The connection string is write-only.</summary>
public sealed class SaveDatabaseConnectionRequest
{
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    /// <summary>Plaintext connection string. On update, leave null/empty to keep the stored one.</summary>
    public string? ConnectionString { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>On update, true means the supplied ConnectionString replaces the stored secret.</summary>
    public bool ReplaceConnectionString { get; set; }
}

public sealed class CreateDatabaseConnectionCommand : IRequest<DatabaseConnectionResult>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public SaveDatabaseConnectionRequest Request { get; set; } = new();
}

public sealed class UpdateDatabaseConnectionCommand : IRequest<DatabaseConnectionResult>
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public SaveDatabaseConnectionRequest Request { get; set; } = new();
}

public sealed class DeleteDatabaseConnectionCommand : IRequest<bool>
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
}

public sealed class TestDatabaseConnectionCommand : IRequest<DatabaseConnectionResult>
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
}

/// <summary>Outcome of a create/update/test. Never carries the connection string.</summary>
public sealed class DatabaseConnectionResult
{
    public bool Success { get; set; }
    public Guid? Id { get; set; }
    public string? Error { get; set; }

    public static DatabaseConnectionResult Ok(Guid id) => new() { Success = true, Id = id };
    public static DatabaseConnectionResult Fail(string error) => new() { Success = false, Error = error };
}
