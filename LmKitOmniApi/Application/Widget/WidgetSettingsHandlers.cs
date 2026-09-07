using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Widget;

public sealed class GetWidgetSettingsQuery : IRequest<WidgetSettingsDto?>
{
    public Guid TenantId { get; init; }
}

public sealed class WidgetSettingsDto
{
    public bool IsActive { get; set; }
    public List<string> AllowedOrigins { get; set; } = [];
    public int RequestsPerMinute { get; set; }
    public int RequestsPerDay { get; set; }
    public string? WidgetTitle { get; set; }
    public string? WelcomeMessage { get; set; }
    public string? BrandColor { get; set; }
    public string? LogoUrl { get; set; }
    public string Position { get; set; } = "bottom-right";
    public DateTime? RotatedAtUtc { get; set; }
}

public sealed class GetWidgetSettingsQueryHandler : IRequestHandler<GetWidgetSettingsQuery, WidgetSettingsDto?>
{
    private readonly HermesDbContext _db;

    public GetWidgetSettingsQueryHandler(HermesDbContext db) => _db = db;

    public async Task<WidgetSettingsDto?> Handle(GetWidgetSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _db.TenantWidgetSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == request.TenantId, cancellationToken);
        if (settings is null) return null;

        return new WidgetSettingsDto
        {
            IsActive = settings.IsActive,
            AllowedOrigins = WidgetOrigins.Parse(settings.AllowedOriginsJson),
            RequestsPerMinute = settings.RequestsPerMinute,
            RequestsPerDay = settings.RequestsPerDay,
            WidgetTitle = settings.WidgetTitle,
            WelcomeMessage = settings.WelcomeMessage,
            BrandColor = settings.BrandColor,
            LogoUrl = settings.LogoUrl,
            Position = settings.Position,
            RotatedAtUtc = settings.RotatedAtUtc
        };
    }
}

public sealed class UpdateWidgetSettingsCommand : IRequest<WidgetMutationResult>
{
    public Guid TenantId { get; init; }
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

public sealed class RotateWidgetKeyCommand : IRequest<WidgetMutationResult>
{
    public Guid TenantId { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

public enum WidgetMutationStatus { Success, ValidationFailed }

public sealed class WidgetMutationResult
{
    public WidgetMutationStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Raw widget key — populated ONLY on rotate, shown exactly once.</summary>
    public string? RawKey { get; init; }
}

public sealed class UpdateWidgetSettingsCommandHandler : IRequestHandler<UpdateWidgetSettingsCommand, WidgetMutationResult>
{
    private const int MaxOrigins = 20;
    private const int MaxRequestsPerMinute = 600;
    private const int MaxRequestsPerDay = 100_000;

    private readonly HermesDbContext _db;

    public UpdateWidgetSettingsCommandHandler(HermesDbContext db) => _db = db;

    public async Task<WidgetMutationResult> Handle(UpdateWidgetSettingsCommand request, CancellationToken cancellationToken)
    {
        var origins = request.AllowedOrigins
            .Select(WidgetOrigins.Normalize)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (origins.Count > MaxOrigins)
            return Invalid($"Tối đa {MaxOrigins} origin được cho phép.");

        if (request.RequestsPerMinute is < 0 or > MaxRequestsPerMinute)
            return Invalid($"RequestsPerMinute phải nằm trong khoảng 0 (không giới hạn) đến {MaxRequestsPerMinute}.");
        if (request.RequestsPerDay is < 0 or > MaxRequestsPerDay)
            return Invalid($"RequestsPerDay phải nằm trong khoảng 0 (không giới hạn) đến {MaxRequestsPerDay}.");

        var brandColor = request.BrandColor?.Trim();
        if (brandColor is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(brandColor, "^#[0-9a-fA-F]{6}$"))
            return Invalid("BrandColor phải có dạng #RRGGBB.");

        var position = request.Position?.Trim() ?? "bottom-right";
        if (position is not ("bottom-right" or "bottom-left"))
            return Invalid("Position chỉ nhận 'bottom-right' hoặc 'bottom-left'.");

        var originsJson = WidgetOrigins.Serialize(origins);

        void Apply(TenantWidgetSettings target)
        {
            target.IsActive = request.IsActive;
            target.AllowedOriginsJson = originsJson;
            target.RequestsPerMinute = request.RequestsPerMinute ?? target.RequestsPerMinute;
            target.RequestsPerDay = request.RequestsPerDay ?? target.RequestsPerDay;
            target.WidgetTitle = request.WidgetTitle?.Trim() is { Length: > 0 } title ? title : null;
            target.WelcomeMessage = request.WelcomeMessage?.Trim() is { Length: > 0 } welcome ? welcome : null;
            target.BrandColor = brandColor is { Length: > 0 } ? brandColor : null;
            target.LogoUrl = request.LogoUrl?.Trim() is { Length: > 0 } logo ? logo : null;
            target.Position = position;
            target.UpdatedAtUtc = DateTime.UtcNow;
        }

        var settings = await _db.TenantWidgetSettings
            .SingleOrDefaultAsync(s => s.TenantId == request.TenantId, cancellationToken);
        var inserting = settings is null;
        if (settings is null)
        {
            settings = new TenantWidgetSettings { TenantId = request.TenantId };
            _db.TenantWidgetSettings.Add(settings);
        }

        Apply(settings);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (inserting && IsTenantUniqueViolation(ex))
        {
            // Lost the read-then-insert race: a concurrent PUT inserted the row first
            // and the unique index on TenantId rejected ours. Drop the loser, re-read
            // the winner and apply this request on top of it (last write wins) instead
            // of leaving the tenant with two rows — which would make every later
            // SingleOrDefaultAsync throw and brick the widget permanently.
            _db.Entry(settings).State = EntityState.Detached;
            var winner = await _db.TenantWidgetSettings
                .SingleAsync(s => s.TenantId == request.TenantId, cancellationToken);
            Apply(winner);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new WidgetMutationResult { Status = WidgetMutationStatus.Success };
    }

    /// <summary>
    /// True when the failure is the unique-index violation on
    /// <c>tenant_widget_settings.TenantId</c> (PostgreSQL 23505 / SQLite
    /// SQLITE_CONSTRAINT). Any other database failure keeps propagating.
    /// </summary>
    private static bool IsTenantUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        Npgsql.PostgresException postgres => postgres.SqlState == "23505",
        Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode == 19,
        _ => false
    };

    private static WidgetMutationResult Invalid(string message) => new()
    {
        Status = WidgetMutationStatus.ValidationFailed,
        ErrorMessage = message
    };
}

public sealed class RotateWidgetKeyCommandHandler : IRequestHandler<RotateWidgetKeyCommand, WidgetMutationResult>
{
    private readonly HermesDbContext _db;

    public RotateWidgetKeyCommandHandler(HermesDbContext db) => _db = db;

    public async Task<WidgetMutationResult> Handle(RotateWidgetKeyCommand request, CancellationToken cancellationToken)
    {
        var settings = await _db.TenantWidgetSettings
            .SingleOrDefaultAsync(s => s.TenantId == request.TenantId, cancellationToken);

        // Refuse rotation until the widget is explicitly configured: the key must
        // never exist before an origin allowlist has been chosen (fail closed).
        if (settings is null)
            return Invalid("Cấu hình widget chưa tồn tại. Vui lòng lưu cấu hình trước khi tạo khóa.");

        // Rotation alone does not enable the widget: admins must still pass an
        // allowlist and IsActive through UpdateWidgetSettings.
        if (!settings.IsActive)
            return Invalid("Widget đang tắt. Bật widget trong cấu hình trước khi tạo khóa.");

        var rawKey = WidgetSecrets.Generate();
        settings.WidgetApiKey = string.Empty;
        settings.WidgetApiKeyHash = WidgetSecrets.Hash(rawKey);
        settings.RotatedAtUtc = request.NowUtc;
        settings.UpdatedAtUtc = request.NowUtc;

        await _db.SaveChangesAsync(cancellationToken);
        return new WidgetMutationResult { Status = WidgetMutationStatus.Success, RawKey = rawKey };
    }

    private static WidgetMutationResult Invalid(string message) => new()
    {
        Status = WidgetMutationStatus.ValidationFailed,
        ErrorMessage = message
    };
}
