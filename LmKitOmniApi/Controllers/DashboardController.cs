using LmKitOmniApi.Application.Dashboard;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Dashboard vận hành. Đây là endpoint MỌI vai trò đều gọi được vì response luôn chứa
/// <c>myUsage</c> (số liệu của chính người gọi); khối <c>cockpit</c> chỉ Admin nhận, và việc
/// cắt bỏ được quyết định trong query handler — không phải ở đây — nên không thể vô tình lộ
/// số liệu toàn hệ thống khi controller bị sửa.
///
/// Trong hệ này <c>Admin</c> là quản trị TOÀN hệ thống (xem <see cref="UsersController"/>:
/// mặc định liệt kê user của mọi tenant), nên cockpit của Admin trải trên mọi đơn vị.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public DashboardController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Số liệu tổng quan. <paramref name="days"/> chỉ nhận 7/30/90 (giá trị khác → 30).
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats([FromQuery] int? days, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var result = await _mediator.Send(new GetDashboardStatsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            IsAdmin = User.IsInRole("Admin"),
            PeriodDays = DashboardPeriod.Normalize(days)
        }, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Báo cáo CSV (đơn vị × model) của kỳ đang chọn. Chỉ Admin — đây là dữ liệu của MỌI đơn vị
    /// nên không thể mở cho Member như endpoint stats.
    /// </summary>
    [HttpGet("export.csv")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ExportCsv([FromQuery] int? days, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out _, out _)) return Unauthorized();

        var result = await _mediator.Send(new GetDashboardCsvQuery
        {
            PeriodDays = DashboardPeriod.Normalize(days)
        }, cancellationToken);

        return File(result.Content, "text/csv; charset=utf-8", result.FileName);
    }
}
