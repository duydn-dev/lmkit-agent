using LmKitOmniApi.Application.Common;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Tenants;

/// <summary>Getlist chuẩn: phân trang + tìm theo tên, mới nhất trước.</summary>
public sealed class ListTenantsQuery : IRequest<PagedResult<TenantDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Paging.DefaultPageSize;
    public string? Search { get; set; }
}

/// <summary>Toàn bộ tenant, gọn, cho dropdown (số tenant thực tế nhỏ).</summary>
public sealed class GetTenantOptionsQuery : IRequest<List<TenantOptionDto>>;

/// <summary>Logo tenant đã giải mã sẵn content-type — null khi tenant không có logo.</summary>
public sealed record TenantLogo(byte[] Data, string ContentType);

/// <summary>Đọc bytes logo của MỘT tenant. Chỉ endpoint phục vụ ảnh mới gọi (kéo cả blob).</summary>
public sealed class GetTenantLogoQuery : IRequest<TenantLogo?>
{
    public Guid TenantId { get; set; }
}

public sealed class ListTenantsQueryHandler : IRequestHandler<ListTenantsQuery, PagedResult<TenantDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListTenantsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<TenantDto>> Handle(ListTenantsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Tenants.AsNoTracking();

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(t => EF.Functions.Like(t.Name, pattern));
        }

        return await query
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TenantDto
            {
                Id = t.Id,
                Name = t.Name,
                AgentDisplayName = t.AgentDisplayName,
                // Chỉ đọc CỜ có-logo qua SQL "LogoData IS NOT NULL" — không nạp mảng byte về.
                HasLogo = t.LogoData != null,
                LogoUpdatedAt = t.LogoUpdatedAt,
                CreatedAt = t.CreatedAt,
                UserCount = _dbContext.Users.Count(u => u.TenantId == t.Id),
                DatabaseConnectionCount = _dbContext.DatabaseConnections.Count(c => c.TenantId == t.Id)
            })
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}

public sealed class GetTenantLogoQueryHandler : IRequestHandler<GetTenantLogoQuery, TenantLogo?>
{
    private readonly HermesDbContext _dbContext;

    public GetTenantLogoQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<TenantLogo?> Handle(GetTenantLogoQuery request, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Tenants.AsNoTracking()
            .Where(t => t.Id == request.TenantId && t.LogoData != null)
            .Select(t => new { t.LogoData, t.LogoContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (row?.LogoData is null) return null;

        var contentType = string.IsNullOrWhiteSpace(row.LogoContentType)
            ? "application/octet-stream"
            : row.LogoContentType;
        return new TenantLogo(row.LogoData, contentType);
    }
}

public sealed class GetTenantOptionsQueryHandler : IRequestHandler<GetTenantOptionsQuery, List<TenantOptionDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetTenantOptionsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public Task<List<TenantOptionDto>> Handle(GetTenantOptionsQuery request, CancellationToken cancellationToken)
        => _dbContext.Tenants.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantOptionDto { Id = t.Id, Name = t.Name })
            .ToListAsync(cancellationToken);
}
