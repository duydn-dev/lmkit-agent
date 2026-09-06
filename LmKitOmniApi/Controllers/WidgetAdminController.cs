using LmKitOmniApi.Application.Widget;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Tenant-admin management of the PUBLIC embeddable chat widget: settings
/// (enable/disable, origin allowlist, quotas, branding) and key rotation.
/// The raw key is returned EXACTLY ONCE at rotation and only its SHA-256 hash
/// is persisted (see <see cref="WidgetSecrets"/>).
/// </summary>
[ApiController]
[Route("api/admin/widget")]
[Authorize(Roles = "Admin")]
public sealed class WidgetAdminController(IMediator mediator) : ApiControllerBase
{
    public sealed class UpdateWidgetSettingsRequest
    {
        public bool IsActive { get; init; }
        public List<string> AllowedOrigins { get; init; } = [];
        public int? RequestsPerMinute { get; init; }
        public int? RequestsPerDay { get; init; }
        public string? WidgetTitle { get; init; }
        public string? WelcomeMessage { get; init; }
        public string? BrandColor { get; init; }
        public string? LogoUrl { get; init; }
        public string? Position { get; init; }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var settings = await mediator.Send(new GetWidgetSettingsQuery { TenantId = tenantId }, ct);
        // Absent row reads as an all-off default so the admin UI can render a form.
        return Ok(settings ?? new WidgetSettingsDto());
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateWidgetSettingsRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await mediator.Send(new UpdateWidgetSettingsCommand
        {
            TenantId = tenantId,
            IsActive = request.IsActive,
            AllowedOrigins = request.AllowedOrigins,
            RequestsPerMinute = request.RequestsPerMinute,
            RequestsPerDay = request.RequestsPerDay,
            WidgetTitle = request.WidgetTitle,
            WelcomeMessage = request.WelcomeMessage,
            BrandColor = request.BrandColor,
            LogoUrl = request.LogoUrl,
            Position = request.Position
        }, ct);

        if (result.Status == WidgetMutationStatus.ValidationFailed)
            return BadRequest(new { message = result.ErrorMessage });
        return NoContent();
    }

    [HttpPost("credentials:rotate")]
    public async Task<IActionResult> RotateKey(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await mediator.Send(new RotateWidgetKeyCommand { TenantId = tenantId }, ct);
        if (result.Status == WidgetMutationStatus.ValidationFailed)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { rawKey = result.RawKey, rotatedAtUtc = DateTime.UtcNow });
    }
}
