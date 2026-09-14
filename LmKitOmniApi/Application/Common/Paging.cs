using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Common;

/// <summary>
/// Chuẩn getlist dùng chung cho MỌI màn quản lý: phân trang 1-based + tìm kiếm.
/// Controllers bind <c>page/pageSize/search</c> từ query string, chuẩn hóa qua
/// <see cref="Normalize"/> (chặn pageSize vô hạn), handler trả
/// <see cref="PagedResult{T}"/> để client render DataTable lazy.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Chuẩn hóa page/pageSize về khoảng hợp lệ (page ≥ 1, 1 ≤ pageSize ≤ 100).</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
    {
        var p = page.GetValueOrDefault(1);
        if (p < 1) p = 1;
        var size = pageSize.GetValueOrDefault(DefaultPageSize);
        if (size < 1) size = DefaultPageSize;
        if (size > MaxPageSize) size = MaxPageSize;
        return (p, size);
    }

    /// <summary>
    /// Materialize một trang từ query ĐÃ lọc + ĐÃ sắp xếp. Đếm tổng trước rồi mới
    /// Skip/Take — hai round-trip, nhưng đúng với mọi provider (Npgsql lẫn SQLite test).
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = total == 0
            ? []
            : await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }
}

/// <summary>Một trang kết quả getlist. Shape cố định — client dựa vào camelCase JSON.</summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
