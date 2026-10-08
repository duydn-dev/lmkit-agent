using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Quotas;

/// <summary>Tất cả gói (kể cả gói đã ngừng dùng) — danh sách nhỏ nên không phân trang.</summary>
public sealed class ListPlansQuery : IRequest<List<PlanDto>>;

/// <summary>Hạn mức hiện tại của MỌI đơn vị, kèm gói đang gán và số dư.</summary>
public sealed class ListTenantQuotasQuery : IRequest<List<TenantQuotaAdminDto>>;

/// <summary>Các grant đã cấp cho một đơn vị, mới nhất trước.</summary>
public sealed class ListGrantsQuery : IRequest<List<GrantDto>>
{
    public Guid TenantId { get; set; }
}

public sealed class ListPlansQueryHandler : IRequestHandler<ListPlansQuery, List<PlanDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListPlansQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public Task<List<PlanDto>> Handle(ListPlansQuery request, CancellationToken cancellationToken)
        => _dbContext.Plans.AsNoTracking()
            // Gói còn dùng lên trước: đó là những gói admin thật sự cần gán.
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.Name)
            .Select(p => new PlanDto
            {
                Id = p.Id,
                Name = p.Name,
                MonthlyTokenLimit = p.MonthlyTokenLimit,
                IsActive = p.IsActive,
                CreatedAtUtc = p.CreatedAtUtc,
                TenantCount = _dbContext.Subscriptions.Count(s => s.PlanId == p.Id && s.IsActive)
            })
            .ToListAsync(cancellationToken);
}

public sealed class ListTenantQuotasQueryHandler : IRequestHandler<ListTenantQuotasQuery, List<TenantQuotaAdminDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListTenantQuotasQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<List<TenantQuotaAdminDto>> Handle(
        ListTenantQuotasQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var monthStart = TenantQuotaUsage.MonthStartUtc(now);

        // CÙNG nguồn với dashboard và với worker cảnh báo — nếu màn quản trị tự đếm riêng thì nó
        // sẽ nói một đằng, thông báo nói một nẻo.
        var usedByTenant = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(_dbContext, monthStart, cancellationToken);

        var tenants = await _dbContext.Tenants.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);

        var subscriptions = await _dbContext.Subscriptions.AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new
            {
                s.Id,
                s.TenantId,
                s.PlanId,
                PlanName = s.Plan!.Name,
                Limit = s.Plan!.MonthlyTokenLimit,
                s.RenewalAtUtc
            })
            .ToListAsync(cancellationToken);

        // Bất biến "một gói active mỗi đơn vị" được giữ ở tầng ghi; dữ liệu cũ vi phạm thì dòng
        // đến sau thắng, đúng luật của khối hạn mức trên dashboard.
        var subscriptionByTenant = new Dictionary<Guid, (Guid Id, Guid PlanId, string PlanName, int Limit, DateTime? Renewal)>();
        foreach (var subscription in subscriptions)
        {
            subscriptionByTenant[subscription.TenantId] =
                (subscription.Id, subscription.PlanId, subscription.PlanName, subscription.Limit, subscription.RenewalAtUtc);
        }

        var credits = await _dbContext.TenantCredits.AsNoTracking()
            .ToDictionaryAsync(c => c.TenantId, c => c.Balance, cancellationToken);

        // Grant gom MỘT lần rồi tính trong bộ nhớ: một đơn vị có thể có nhiều grant nên truy vấn
        // theo từng đơn vị sẽ thành N+1. Phần còn hiệu lực mới được tính vào "khả dụng".
        var grants = await _dbContext.TokenGrants.AsNoTracking()
            .Select(g => new { g.TenantId, g.Tokens, g.UsedTokens, g.ExpiresAtUtc })
            .ToListAsync(cancellationToken);

        var grantSummary = grants
            .Where(g => QuotaGrants.IsActive(g.ExpiresAtUtc, g.Tokens, g.UsedTokens, now))
            .GroupBy(g => g.TenantId)
            .ToDictionary(
                group => group.Key,
                group => (Count: group.Count(), Remaining: group.Sum(g => g.Tokens - g.UsedTokens)));

        var rows = new List<TenantQuotaAdminDto>(tenants.Count);
        foreach (var tenant in tenants)
        {
            var hasPlan = subscriptionByTenant.TryGetValue(tenant.Id, out var plan);
            var limit = hasPlan ? plan.Limit : 0;
            var isUnlimited = !hasPlan || limit <= 0;
            var used = usedByTenant.TryGetValue(tenant.Id, out var value) ? value : 0;
            var summary = grantSummary.TryGetValue(tenant.Id, out var found)
                ? found
                : (Count: 0, Remaining: 0);

            rows.Add(new TenantQuotaAdminDto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                SubscriptionId = hasPlan ? plan.Id : null,
                PlanId = hasPlan ? plan.PlanId : null,
                PlanName = hasPlan ? plan.PlanName : null,
                MonthlyTokenLimit = isUnlimited ? 0 : limit,
                RenewalAtUtc = hasPlan ? plan.Renewal : null,
                UsedTokens = used,
                UtilizationPct = isUnlimited ? 0 : QuotaAlertRules.Percent(used, limit),
                IsUnlimited = isUnlimited,
                CreditBalance = credits.TryGetValue(tenant.Id, out var balance) ? balance : 0,
                ActiveGrantCount = summary.Count,
                GrantRemainingTokens = summary.Remaining
            });
        }

        return rows;
    }
}

public sealed class ListGrantsQueryHandler : IRequestHandler<ListGrantsQuery, List<GrantDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListGrantsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<List<GrantDto>> Handle(ListGrantsQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return await _dbContext.TokenGrants.AsNoTracking()
            .Where(g => g.TenantId == request.TenantId)
            .OrderByDescending(g => g.CreatedAtUtc)
            .Select(g => new GrantDto
            {
                Id = g.Id,
                TenantId = g.TenantId,
                Tokens = g.Tokens,
                UsedTokens = g.UsedTokens,
                RemainingTokens = g.Tokens - g.UsedTokens,
                ExpiresAtUtc = g.ExpiresAtUtc,
                Reason = g.Reason,
                CreatedAtUtc = g.CreatedAtUtc,
                IsExpired = g.ExpiresAtUtc != null && g.ExpiresAtUtc <= now
            })
            .ToListAsync(cancellationToken);
    }
}
