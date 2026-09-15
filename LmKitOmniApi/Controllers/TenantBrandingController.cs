using LmKitOmniApi.Application.Tenants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Thương hiệu theo tenant cho NGƯỜI DÙNG đã đăng nhập (mọi vai trò), khác với
/// <see cref="TenantsController"/> chỉ dành cho Admin quản lý mọi tenant.
///
/// Logo phục vụ ở đây luôn là logo của CHÍNH tenant người gọi (đọc từ claim
/// <c>"TenantId"</c>), nên một thành viên không thể dò/đọc logo của tenant khác, và
/// tên/logo/tên-trợ-lý dùng để hiển thị đã đi kèm trong <c>GET /api/auth/me</c>.
/// </summary>
[ApiController]
[Route("api/tenant-branding")]
[Authorize]
public sealed class TenantBrandingController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public TenantBrandingController(IMediator mediator) => _mediator = mediator;

    /// <summary>Logo của tenant đang đăng nhập — render trực tiếp qua &lt;img src&gt; nhờ cookie JWT.</summary>
    [HttpGet("logo")]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var logo = await _mediator.Send(new GetTenantLogoQuery { TenantId = tenantId }, ct);
        return logo is null ? NotFound() : File(logo.Data, logo.ContentType);
    }
}
