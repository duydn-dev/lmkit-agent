using MediatR;

namespace LmKitOmniApi.Application.Users.Queries;

public class GetUsersQuery : IRequest<Common.PagedResult<UserSummaryDto>>
{
    /// <summary>
    /// Tenant muốn liệt kê. <c>null</c> → toàn hệ thống (endpoint này đã Admin-only,
    /// admin quản lý user của mọi tenant). Truyền giá trị cụ thể khi UI lọc theo tenant.
    /// </summary>
    public Guid? TenantId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Common.Paging.DefaultPageSize;
    public string? Search { get; set; }
}

/// <summary>
/// Mirrors the anonymous projection previously built inline in UsersController.GetUsers.
/// Property names and declaration order are load-bearing: System.Text.Json serializes in
/// declaration order with the default camelCase policy, so this shape is wire-identical.
/// </summary>
public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>Tên tenant hiển thị trong bảng quản trị (admin quản lý đa tenant).</summary>
    public string TenantName { get; set; } = string.Empty;
}
