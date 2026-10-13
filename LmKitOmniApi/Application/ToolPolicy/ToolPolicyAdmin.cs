using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Domain.Constants;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.ToolPolicy;

/// <summary>Một tool nhìn từ màn quản trị: mức nền của hệ thống + cấu hình của đơn vị + kết quả cuối.</summary>
public sealed record ToolPolicyEntryDto(
    string ToolName,
    string Tier,
    string Why,
    string BaselineEnforcement,
    string? TenantEnforcement,
    string Reason,
    string EffectiveEnforcement,
    string Source);

/// <summary>Kết quả ghi cấu hình: "ok" / "invalid" (kèm lý do) / "not-found".</summary>
public sealed record ToolPolicyWriteResult(string Status, string? Error, ToolPolicyEntryDto? Entry)
{
    public static ToolPolicyWriteResult Ok(ToolPolicyEntryDto entry) => new("ok", null, entry);

    public static ToolPolicyWriteResult Invalid(string error) => new("invalid", error, null);

    public static ToolPolicyWriteResult NotFound() => new("not-found", null, null);
}

/// <summary>Danh sách chính sách tool đang có hiệu lực của một đơn vị.</summary>
public sealed class ListToolPoliciesQuery : IRequest<List<ToolPolicyEntryDto>>
{
    public Guid TenantId { get; set; }
}

public sealed class ListToolPoliciesQueryHandler : IRequestHandler<ListToolPoliciesQuery, List<ToolPolicyEntryDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListToolPoliciesQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<List<ToolPolicyEntryDto>> Handle(
        ListToolPoliciesQuery request, CancellationToken cancellationToken)
    {
        var overrides = await _dbContext.TenantToolPolicies.AsNoTracking()
            .Where(p => p.TenantId == request.TenantId)
            .ToDictionaryAsync(p => p.ToolName, p => p, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return ToolRiskCatalog.All
            .OrderBy(e => ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.DefaultForTier(e.Tier)))
            .ThenBy(e => e.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(e => Build(e.ToolName, e.Tier, e.Why, overrides))
            .ToList();
    }

    internal static ToolPolicyEntryDto Build(
        string toolName, string tier, string why, IReadOnlyDictionary<string, TenantToolPolicy> overrides)
    {
        var baseline = ToolPolicyEnforcement.DefaultForTier(tier);
        var decision = overrides.TryGetValue(toolName, out var row)
            ? ToolRiskDecision.Apply(
                new ToolRiskDecision(toolName, tier, baseline, baseline, "default", null),
                row.Enforcement,
                row.Reason)
            : new ToolRiskDecision(toolName, tier, baseline, baseline, "default", null);

        var tenantEnforcement = overrides.TryGetValue(toolName, out var stored)
            ? ToolPolicyEnforcement.TryParse(stored.Enforcement)
            : null;

        return new ToolPolicyEntryDto(
            toolName,
            tier,
            why,
            baseline,
            tenantEnforcement,
            decision.Reason ?? string.Empty,
            decision.Enforcement,
            decision.Source);
    }
}

/// <summary>Đặt (siết thêm) chính sách cho một tool của đơn vị.</summary>
public sealed class SetToolPolicyCommand : IRequest<ToolPolicyWriteResult>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string Enforcement { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class SetToolPolicyCommandHandler
    : IRequestHandler<SetToolPolicyCommand, ToolPolicyWriteResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly IToolRiskPolicy _riskPolicy;

    public SetToolPolicyCommandHandler(HermesDbContext dbContext, IToolRiskPolicy riskPolicy)
    {
        _dbContext = dbContext;
        _riskPolicy = riskPolicy;
    }

    public async Task<ToolPolicyWriteResult> Handle(
        SetToolPolicyCommand request, CancellationToken cancellationToken)
    {
        var validation = ToolPolicyMutation.Validate(request.ToolName, request.Enforcement);
        if (!validation.IsAllowed)
        {
            return ToolPolicyWriteResult.Invalid(validation.RejectionReason!);
        }

        var catalogEntry = ToolRiskCatalog.All.First(
            e => string.Equals(e.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase));

        var existing = await _dbContext.TenantToolPolicies
            .FirstOrDefaultAsync(
                p => p.TenantId == request.TenantId
                     && p.ToolName.ToLower() == catalogEntry.ToolName.ToLower(),
                cancellationToken);

        if (existing is null)
        {
            existing = new TenantToolPolicy
            {
                TenantId = request.TenantId,
                ToolName = catalogEntry.ToolName,
                Enforcement = validation.Enforcement!,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                UpdatedByUserId = request.UserId,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _dbContext.TenantToolPolicies.Add(existing);
        }
        else
        {
            existing.Enforcement = validation.Enforcement!;
            existing.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            existing.UpdatedByUserId = request.UserId;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }

        // SaveChanges đi qua AuditSaveChangesInterceptor → thay đổi chính sách an toàn được ghi
        // vào audit_logs kèm actor và giá trị cũ/mới, không cần mã ghi log riêng.
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Replica này phải thấy cấu hình mới NGAY (worker duyệt run chạy cùng tiến trình này), thay vì
        // chờ hết TTL cache.
        _riskPolicy.Invalidate(request.TenantId);

        var overrides = await _dbContext.TenantToolPolicies.AsNoTracking()
            .Where(p => p.TenantId == request.TenantId)
            .ToDictionaryAsync(p => p.ToolName, p => p, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return ToolPolicyWriteResult.Ok(
            ListToolPoliciesQueryHandler.Build(catalogEntry.ToolName, catalogEntry.Tier, catalogEntry.Why, overrides));
    }
}

/// <summary>Bỏ cấu hình của đơn vị cho một tool → tool trở về mức mặc định của hệ thống.</summary>
public sealed class ClearToolPolicyCommand : IRequest<ToolPolicyWriteResult>
{
    public Guid TenantId { get; set; }
    public string ToolName { get; set; } = string.Empty;
}

public sealed class ClearToolPolicyCommandHandler
    : IRequestHandler<ClearToolPolicyCommand, ToolPolicyWriteResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly IToolRiskPolicy _riskPolicy;

    public ClearToolPolicyCommandHandler(HermesDbContext dbContext, IToolRiskPolicy riskPolicy)
    {
        _dbContext = dbContext;
        _riskPolicy = riskPolicy;
    }

    public async Task<ToolPolicyWriteResult> Handle(
        ClearToolPolicyCommand request, CancellationToken cancellationToken)
    {
        var catalogEntry = ToolRiskCatalog.All.FirstOrDefault(
            e => string.Equals(e.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase));

        if (catalogEntry is null) return ToolPolicyWriteResult.NotFound();

        var existing = await _dbContext.TenantToolPolicies
            .FirstOrDefaultAsync(
                p => p.TenantId == request.TenantId
                     && p.ToolName.ToLower() == catalogEntry.ToolName.ToLower(),
                cancellationToken);

        if (existing is not null)
        {
            _dbContext.TenantToolPolicies.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _riskPolicy.Invalidate(request.TenantId);
        }

        var overrides = await _dbContext.TenantToolPolicies.AsNoTracking()
            .Where(p => p.TenantId == request.TenantId)
            .ToDictionaryAsync(p => p.ToolName, p => p, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return ToolPolicyWriteResult.Ok(
            ListToolPoliciesQueryHandler.Build(catalogEntry.ToolName, catalogEntry.Tier, catalogEntry.Why, overrides));
    }
}
