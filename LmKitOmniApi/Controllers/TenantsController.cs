using LmKitOmniApi.Application.Common;
using LmKitOmniApi.Application.Tenants;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Quản lý tenant (Admin). Getlist chuẩn phân trang + tìm kiếm; xóa chỉ được
/// với tenant RỖNG (command tự kiểm tra) — không bao giờ cascade dữ liệu.
/// <c>GET /options</c> là nguồn cho các dropdown gán tenant (vd. kết nối CSDL).
/// </summary>
[ApiController]
[Route("api/tenants")]
[Authorize(Roles = "Admin")]
public sealed class TenantsController : ApiControllerBase
{
    private const long MaxLogoBytes = 2 * 1024 * 1024; // 2 MB

    // Chỉ nhận ảnh raster có chữ ký byte kiểm chứng được. SVG bị loại CHỦ ĐÍCH: nó có
    // thể nhúng script và bị phục vụ inline → nguy cơ XSS; content-type dùng cho response
    // lấy từ đây (theo phần mở rộng đã kiểm), không tin content-type client gửi lên.
    private static readonly Dictionary<string, string> AllowedLogoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
    };

    private readonly IMediator _mediator;

    public TenantsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] string? search, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        return Ok(await _mediator.Send(new ListTenantsQuery { Page = p, PageSize = size, Search = search }, ct));
    }

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
        => Ok(await _mediator.Send(new GetTenantOptionsQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveTenantRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new CreateTenantCommand { Request = request }, ct);
        return result.Success ? Ok(new { id = result.Id }) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveTenantRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdateTenantCommand { Id = id, Request = request }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy tenant." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var actorTenantId)) return Unauthorized();
        var result = await _mediator.Send(new DeleteTenantCommand { Id = id, ActorTenantId = actorTenantId }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy tenant." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    /// <summary>Xem logo của một tenant bất kỳ (admin) — dùng cho ô xem trước ở form quản lý.</summary>
    [HttpGet("{id:guid}/logo")]
    public async Task<IActionResult> GetLogo(Guid id, CancellationToken ct)
    {
        var logo = await _mediator.Send(new GetTenantLogoQuery { TenantId = id }, ct);
        return logo is null ? NotFound() : File(logo.Data, logo.ContentType);
    }

    /// <summary>Tải lên/thay logo tenant. Kiểm tra phần mở rộng + chữ ký byte + kích thước
    /// trước khi lưu; content-type suy ra từ phần mở rộng đã kiểm, không tin client.</summary>
    [HttpPost("{id:guid}/logo")]
    public async Task<IActionResult> UploadLogo(Guid id, IFormFile? logo, CancellationToken ct)
    {
        if (logo is null || logo.Length == 0)
            return BadRequest(new { message = "Chưa chọn file logo." });
        if (logo.Length > MaxLogoBytes)
            return BadRequest(new { message = $"Logo tối đa {MaxLogoBytes / (1024 * 1024)}MB." });

        var ext = Path.GetExtension(logo.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedLogoTypes.TryGetValue(ext, out var contentType))
            return BadRequest(new { message = "Định dạng logo phải là PNG, JPG, WEBP, GIF hoặc BMP." });

        if (!await UploadFileValidator.HasExpectedSignatureAsync(logo, ext, ct))
            return BadRequest(new { message = "Nội dung file không khớp định dạng ảnh khai báo." });

        using var buffer = new MemoryStream();
        await logo.CopyToAsync(buffer, ct);

        var result = await _mediator.Send(
            new SetTenantLogoCommand { Id = id, Data = buffer.ToArray(), ContentType = contentType }, ct);
        if (result.Success) return NoContent();
        return result.Error == "Không tìm thấy tenant." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
    }

    /// <summary>Gỡ logo của tenant (quay về logo mặc định của hệ thống).</summary>
    [HttpDelete("{id:guid}/logo")]
    public async Task<IActionResult> DeleteLogo(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new ClearTenantLogoCommand { Id = id }, ct);
        if (result.Success) return NoContent();
        return NotFound(new { message = result.Error });
    }
}
