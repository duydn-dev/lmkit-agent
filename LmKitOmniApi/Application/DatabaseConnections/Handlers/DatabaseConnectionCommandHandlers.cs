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
        if (!databases.TryParseProvider(request.Provider, out _) && !MongoDatabaseService.Handles(request.Provider))
            return "Loại cơ sở dữ liệu không được hỗ trợ.";
        if (requireConnectionString && string.IsNullOrWhiteSpace(request.ConnectionString))
            return "Chuỗi kết nối là bắt buộc.";
        return null;
    }

    /// <summary>
    /// Resolve gán tenant từ request: IsGlobal → null (toàn hệ thống); TenantId có
    /// giá trị → tenant đó (phải tồn tại); còn lại → fallback (tenant của admin
    /// khi tạo mới / gán hiện tại khi cập nhật). Trả (ok, value, error).
    /// </summary>
    public static async Task<(bool Ok, Guid? TenantId, string? Error)> ResolveTenantAssignmentAsync(
        HermesDbContext dbContext, SaveDatabaseConnectionRequest request, Guid? fallback, CancellationToken ct)
    {
        if (request.IsGlobal) return (true, null, null);
        if (request.TenantId is { } explicitTenant)
        {
            var exists = await dbContext.Tenants.AnyAsync(t => t.Id == explicitTenant, ct);
            return exists ? (true, explicitTenant, null) : (false, null, "Tenant được gán không tồn tại.");
        }
        return (true, fallback, null);
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

        var (assignOk, assignedTenantId, assignError) = await DatabaseConnectionValidation
            .ResolveTenantAssignmentAsync(_dbContext, request, command.TenantId, ct);
        if (!assignOk) return DatabaseConnectionResult.Fail(assignError!);

        // Unique theo phạm vi gán: trong một tenant, hoặc trong nhóm toàn hệ thống.
        // (Index DB (TenantId, Name) unique không chặn được hai hàng null — check tại đây.)
        var duplicate = await _dbContext.DatabaseConnections
            .AnyAsync(c => c.TenantId == assignedTenantId && c.Name == request.Name.Trim(), ct);
        if (duplicate) return DatabaseConnectionResult.Fail("Đã tồn tại kết nối cùng tên trong phạm vi này.");

        var entity = new DatabaseConnection
        {
            TenantId = assignedTenantId,
            UserId = command.UserId,
            Name = request.Name.Trim(),
            Provider = request.Provider.Trim(),
            ConnectionStringProtected = _protector.Protect(request.ConnectionString!.Trim()),
            IsActive = request.IsActive,
            AllowWrites = request.AllowWrites
        };
        // MongoDB is schemaless — nothing to index into Qdrant; it is queryable at once
        // and its schema is sampled live per request. Mark it "indexed" so the agent's
        // connection resolver (which requires IsIndexed) can use it and the worker skips it.
        if (MongoDatabaseService.Handles(entity.Provider))
        {
            entity.IsIndexed = true;
            entity.IndexStatus = "Completed";
        }
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

        // Scoped lookup: hàng của tenant mình HOẶC hàng toàn hệ thống; id lạ trông
        // hệt như không tồn tại.
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && (c.TenantId == command.TenantId || c.TenantId == null), ct);
        if (entity is null) return DatabaseConnectionResult.Fail("Không tìm thấy kết nối.");

        var (assignOk, assignedTenantId, assignError) = await DatabaseConnectionValidation
            .ResolveTenantAssignmentAsync(_dbContext, request, entity.TenantId, ct);
        if (!assignOk) return DatabaseConnectionResult.Fail(assignError!);

        var newName = request.Name.Trim();
        var duplicate = await _dbContext.DatabaseConnections
            .AnyAsync(c => c.Id != entity.Id && c.TenantId == assignedTenantId && c.Name == newName, ct);
        if (duplicate) return DatabaseConnectionResult.Fail("Đã tồn tại kết nối cùng tên trong phạm vi này.");

        // Đổi phạm vi gán → collection Qdrant đổi tên (segment tenant/global) → bắt
        // buộc re-index để schema nằm đúng collection mới.
        var scopeChanged = entity.TenantId != assignedTenantId;
        entity.TenantId = assignedTenantId;
        entity.Name = newName;
        entity.Provider = request.Provider.Trim();
        entity.IsActive = request.IsActive;
        entity.AllowWrites = request.AllowWrites;
        if (replacing)
        {
            entity.ConnectionStringProtected = _protector.Protect(request.ConnectionString!.Trim());
        }
        if (replacing || scopeChanged)
        {
            // Credentials/target/phạm vi gán đổi → schema đã index có thể sai chỗ/cũ.
            entity.IsIndexed = false;
            entity.IndexStatus = "Pending";
        }
        // MongoDB is never Qdrant-indexed (sampled live) — keep it usable and out of the
        // worker regardless of a connection-string change or a switch to Mongo.
        if (MongoDatabaseService.Handles(entity.Provider))
        {
            entity.IsIndexed = true;
            entity.IndexStatus = "Completed";
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
            .FirstOrDefaultAsync(c => c.Id == command.Id && (c.TenantId == command.TenantId || c.TenantId == null), ct);
        if (entity is null) return false;
        _dbContext.DatabaseConnections.Remove(entity);
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class ReindexDatabaseConnectionCommandHandler : IRequestHandler<ReindexDatabaseConnectionCommand, bool>
{
    private readonly HermesDbContext _dbContext;

    public ReindexDatabaseConnectionCommandHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> Handle(ReindexDatabaseConnectionCommand command, CancellationToken ct)
    {
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && (c.TenantId == command.TenantId || c.TenantId == null), ct);
        if (entity is null) return false;

        // MongoDB has no Qdrant index (schema is sampled live) — re-index is a no-op;
        // never flip IsIndexed off or the worker would try to SQL-introspect it.
        if (MongoDatabaseService.Handles(entity.Provider)) return true;

        // Enqueue: reset index state so the background worker re-picks this row.
        entity.IsIndexed = false;
        entity.IndexStatus = "Pending";
        entity.IndexAttempts = 0;
        entity.IndexLeaseUntilUtc = null;
        entity.LastIndexError = null;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class TestDatabaseConnectionCommandHandler : IRequestHandler<TestDatabaseConnectionCommand, DatabaseConnectionResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly DbConnectionSecretProtector _protector;
    private readonly ExternalDatabaseService _databases;
    private readonly MongoDatabaseService _mongo;
    private readonly ILogger<TestDatabaseConnectionCommandHandler> _logger;

    public TestDatabaseConnectionCommandHandler(
        HermesDbContext dbContext, DbConnectionSecretProtector protector, ExternalDatabaseService databases,
        MongoDatabaseService mongo, ILogger<TestDatabaseConnectionCommandHandler> logger)
    {
        _dbContext = dbContext;
        _protector = protector;
        _databases = databases;
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<DatabaseConnectionResult> Handle(TestDatabaseConnectionCommand command, CancellationToken ct)
    {
        var entity = await _dbContext.DatabaseConnections
            .FirstOrDefaultAsync(c => c.Id == command.Id && (c.TenantId == command.TenantId || c.TenantId == null), ct);
        if (entity is null) return DatabaseConnectionResult.Fail("Không tìm thấy kết nối.");

        var isMongo = MongoDatabaseService.Handles(entity.Provider);
        DbProvider provider = default;
        if (!isMongo && !_databases.TryParseProvider(entity.Provider, out provider))
            return DatabaseConnectionResult.Fail("Loại cơ sở dữ liệu không được hỗ trợ.");

        try
        {
            var connectionString = _protector.Unprotect(entity.ConnectionStringProtected);
            var egressDenial = isMongo
                ? await _mongo.TestConnectionAsync(connectionString, ct)
                : await _databases.TestConnectionAsync(provider, connectionString, ct);
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
