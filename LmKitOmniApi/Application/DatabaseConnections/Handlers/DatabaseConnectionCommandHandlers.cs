using LmKitOmniApi.Application.DatabaseConnections.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.DatabaseConnections.Handlers;

internal static class DatabaseConnectionValidation
{
    public const int MaxNameLength = 200;

    public static string? Validate(SaveDatabaseConnectionRequest request, ExternalDatabaseService databases, bool requireConnectionString)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Tên kết nối là bắt buộc.";
        if (request.Name.Length > MaxNameLength) return $"Tên kết nối không được vượt quá {MaxNameLength} ký tự.";
        if (!databases.TryParseProvider(request.Provider, out _)) return "Loại cơ sở dữ liệu không được hỗ trợ.";
        if (requireConnectionString && string.IsNullOrWhiteSpace(request.ConnectionString))
            return "Chuỗi kết nối là bắt buộc.";
        return null;
    }
}

public sealed class CreateDatabaseConnectionCommandHandler : IRequestHandler<CreateDatabaseConnectionCommand, DatabaseConnectionResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly DbConnectionSecretProtector _protector;
    private readonly ExternalDatabaseService _databases;

    public CreateDatabaseConnectionCommandHandler(HermesDbContext dbContext, DbConnectionSecretProtector protector, ExternalDatabaseService databases)
    {
        _dbContext = dbContext;
        _protector = protector;
        _databases = databases;
    }

    public async Task<DatabaseConnectionResult> Handle(CreateDatabaseConnectionCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var error = DatabaseConnectionValidation.Validate(request, _databases, requireConnectionString: true);
        if (error is not null) return DatabaseConnectionResult.Fail(error);

        var duplicate = await _dbContext.DatabaseConnections
            .AnyAsync(c => c.TenantId == command.TenantId && c.Name == request.Name.Trim(), ct);
        if (duplicate) return DatabaseConnectionResult.Fail("Đã tồn tại kết nối cùng tên trong tổ chức.");

        var entity = new DatabaseConnection
        {
            TenantId = command.TenantId,
            UserId = command.UserId,
            Name = request.Name.Trim(),
            Provider = request.Provider.Trim(),
            ConnectionStringProtected = _protector.Protect(request.ConnectionString!.Trim()),
            IsActive = request.IsActive
        };
        _dbContext.DatabaseConnections.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
        return DatabaseConnectionResult.Ok(entity.Id);
    }
}

public sealed class UpdateDatabaseConnectionCommandHandler : IRequestHandler<UpdateDatabaseConnectionCommand, DatabaseConnectionResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly DbConnectionSecretProtector _protector;
    private readonly ExternalDatabaseService _databases;

    public UpdateDatabaseConnectionCommandHandler(HermesDbContext dbContext, DbConnectionSecretProtector protector, ExternalDatabaseService databases)
    {
        _dbContext = dbContext;
        _protector = protector;
        _databases = databases;
    }

    public async Task<DatabaseConnectionResult> Handle(UpdateDatabaseConnectionCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var replacing = request.ReplaceConnectionString && !string.IsNullOrWhiteSpace(request.ConnectionString);
        var error = DatabaseConnectionValidation.Validate(request, _databases, requireConnectionString: replacing);
        if (error is not null) return DatabaseConnectionResult.Fail(error);

        // Tenant-scoped lookup: a foreign id looks exactly like a missing one.
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && c.TenantId == command.TenantId, ct);
        if (entity is null) return DatabaseConnectionResult.Fail("Không tìm thấy kết nối.");

        entity.Name = request.Name.Trim();
        entity.Provider = request.Provider.Trim();
        entity.IsActive = request.IsActive;
        if (replacing)
        {
            entity.ConnectionStringProtected = _protector.Protect(request.ConnectionString!.Trim());
            // Credentials/target changed → the indexed schema may be stale.
            entity.IsIndexed = false;
            entity.IndexStatus = "Pending";
        }
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return DatabaseConnectionResult.Ok(entity.Id);
    }
}

public sealed class DeleteDatabaseConnectionCommandHandler : IRequestHandler<DeleteDatabaseConnectionCommand, bool>
{
    private readonly HermesDbContext _dbContext;

    public DeleteDatabaseConnectionCommandHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> Handle(DeleteDatabaseConnectionCommand command, CancellationToken ct)
    {
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && c.TenantId == command.TenantId, ct);
        if (entity is null) return false;
        _dbContext.DatabaseConnections.Remove(entity);
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class TestDatabaseConnectionCommandHandler : IRequestHandler<TestDatabaseConnectionCommand, DatabaseConnectionResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly DbConnectionSecretProtector _protector;
    private readonly ExternalDatabaseService _databases;
    private readonly ILogger<TestDatabaseConnectionCommandHandler> _logger;

    public TestDatabaseConnectionCommandHandler(
        HermesDbContext dbContext, DbConnectionSecretProtector protector, ExternalDatabaseService databases,
        ILogger<TestDatabaseConnectionCommandHandler> logger)
    {
        _dbContext = dbContext;
        _protector = protector;
        _databases = databases;
        _logger = logger;
    }

    public async Task<DatabaseConnectionResult> Handle(TestDatabaseConnectionCommand command, CancellationToken ct)
    {
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && c.TenantId == command.TenantId, ct);
        if (entity is null) return DatabaseConnectionResult.Fail("Không tìm thấy kết nối.");
        if (!_databases.TryParseProvider(entity.Provider, out var provider))
            return DatabaseConnectionResult.Fail("Loại cơ sở dữ liệu không được hỗ trợ.");

        try
        {
            var connectionString = _protector.Unprotect(entity.ConnectionStringProtected);
            var egressDenial = await _databases.TestConnectionAsync(provider, connectionString, ct);
            if (egressDenial is not null) return DatabaseConnectionResult.Fail(egressDenial);
            return DatabaseConnectionResult.Ok(entity.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database connection test failed for connection {ConnectionId}.", entity.Id);
            var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return DatabaseConnectionResult.Fail($"Kết nối thất bại: {message}");
        }
    }
}
