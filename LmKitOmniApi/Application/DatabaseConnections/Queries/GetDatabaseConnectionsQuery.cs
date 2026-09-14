using LmKitOmniApi.Application.Common;
using MediatR;

namespace LmKitOmniApi.Application.DatabaseConnections.Queries;

/// <summary>
/// Getlist chuẩn cho màn quản lý kết nối CSDL: phân trang + tìm theo tên/loại,
/// mới nhất trước. Trả về kết nối của tenant hiện tại VÀ kết nối toàn hệ thống
/// (TenantId null) — không bao giờ trả chuỗi kết nối.
/// </summary>
public sealed class GetDatabaseConnectionsQuery : IRequest<PagedResult<DatabaseConnectionDto>>
{
    public Guid TenantId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Paging.DefaultPageSize;
    public string? Search { get; set; }
}

public sealed class DatabaseConnectionDto
{
    public Guid Id { get; set; }

    /// <summary>Null = kết nối toàn hệ thống (mọi tenant dùng chung).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Tên tenant sở hữu; null với kết nối toàn hệ thống.</summary>
    public string? TenantName { get; set; }

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
