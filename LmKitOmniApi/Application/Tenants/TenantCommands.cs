using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Tenants;

public sealed record TenantMutationResult(bool Success, string? Error, Guid? Id = null);

public sealed class CreateTenantCommand : IRequest<TenantMutationResult>
{
    public SaveTenantRequest Request { get; set; } = new();
}

public sealed class UpdateTenantCommand : IRequest<TenantMutationResult>
{
    public Guid Id { get; set; }
    public SaveTenantRequest Request { get; set; } = new();
}

/// <summary>
/// Xóa CỨNG một tenant — chỉ khi tenant rỗng (không user, không phiên chat,
/// không tài liệu, không kết nối CSDL). Tenant đang có dữ liệu phải được dọn
/// trước một cách chủ đích; API không bao giờ cascade-xóa dữ liệu nghiệp vụ.
/// </summary>
public sealed class DeleteTenantCommand : IRequest<TenantMutationResult>
{
    public Guid Id { get; set; }
    /// <summary>Tenant của admin đang thao tác — không cho tự xóa tenant của chính mình.</summary>
    public Guid ActorTenantId { get; set; }
}

public sealed class TenantCommandHandlers :
    IRequestHandler<CreateTenantCommand, TenantMutationResult>,
    IRequestHandler<UpdateTenantCommand, TenantMutationResult>,
    IRequestHandler<DeleteTenantCommand, TenantMutationResult>
{
    private const int MaxNameLength = 200;

    private readonly HermesDbContext _dbContext;

    public TenantCommandHandlers(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<TenantMutationResult> Handle(CreateTenantCommand request, CancellationToken cancellationToken)
    {
        var (name, error) = await ValidateNameAsync(request.Request.Name, excludeId: null, cancellationToken);
        if (error is not null) return new TenantMutationResult(false, error);

        var tenant = new Domain.Entities.Tenant { Name = name! };
        _dbContext.Tenants.Add(tenant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TenantMutationResult(true, null, tenant.Id);
    }

    public async Task<TenantMutationResult> Handle(UpdateTenantCommand request, CancellationToken cancellationToken)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (tenant is null) return new TenantMutationResult(false, "Không tìm thấy tenant.");

        var (name, error) = await ValidateNameAsync(request.Request.Name, excludeId: request.Id, cancellationToken);
        if (error is not null) return new TenantMutationResult(false, error);

        tenant.Name = name!;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TenantMutationResult(true, null, tenant.Id);
    }

    public async Task<TenantMutationResult> Handle(DeleteTenantCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == request.ActorTenantId)
            return new TenantMutationResult(false, "Không thể xóa tenant bạn đang đăng nhập.");

        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (tenant is null) return new TenantMutationResult(false, "Không tìm thấy tenant.");

        var hasData =
            await _dbContext.Users.AnyAsync(u => u.TenantId == request.Id, cancellationToken) ||
            await _dbContext.ChatSessions.AnyAsync(s => s.TenantId == request.Id, cancellationToken) ||
            await _dbContext.TenantApiKeys.AnyAsync(k => k.TenantId == request.Id, cancellationToken) ||
            await _dbContext.DatabaseConnections.AnyAsync(c => c.TenantId == request.Id, cancellationToken);
        if (hasData)
            return new TenantMutationResult(false, "Tenant vẫn còn dữ liệu (user/phiên chat/API key/kết nối CSDL) — dọn sạch trước khi xóa.");

        _dbContext.Tenants.Remove(tenant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new TenantMutationResult(true, null, request.Id);
    }

    private async Task<(string? Name, string? Error)> ValidateNameAsync(
        string? rawName, Guid? excludeId, CancellationToken cancellationToken)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "Tên tenant không được để trống.");
        if (name.Length > MaxNameLength) return (null, $"Tên tenant tối đa {MaxNameLength} ký tự.");

        var duplicated = await _dbContext.Tenants
            .AnyAsync(t => t.Name == name && (excludeId == null || t.Id != excludeId), cancellationToken);
        return duplicated ? (null, "Tên tenant đã tồn tại.") : (name, null);
    }
}
