using LmKitOmniApi.Application.Quotas;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.Workers;

/// <summary>
/// Cảnh báo CHỦ ĐỘNG khi một đơn vị chạm ngưỡng hạn mức token tháng
/// (<see cref="Application.Dashboard.DashboardPeriod.OverThresholdPercent"/> = 80%), và một cảnh
/// báo nặng hơn khi đã vượt 100%. Trước đây tình trạng này chỉ nằm im trong danh sách cảnh báo
/// của dashboard — người quản trị phải tự mở màn hình đúng lúc mới thấy, tức là thường thấy sau
/// khi đơn vị đã đốt hết hạn mức.
///
/// Thông báo gửi cho ADMIN của chính đơn vị đó, vì chỉ họ mới cấp thêm hạn mức/nâng gói được.
///
/// Theo đúng khuôn worker trong nhà (<see cref="ScheduledTaskWorker"/>): scope mỗi vòng, mọi lỗi
/// bị nuốt và ghi log để không bao giờ làm sập host. Khác ở chỗ vòng quét chạy NGAY khi khởi
/// động rồi mới theo nhịp <see cref="PollInterval"/>: một bản deploy lại không nên làm cảnh báo
/// biến mất im lặng trong 15 phút đầu.
///
/// Nhịp 15 phút là đủ: hạn mức reset theo THÁNG, còn "đã dùng" chỉ nhảy khi có người thật sự
/// chạy suy luận. Quét dày hơn chỉ tốn truy vấn mà không cảnh báo sớm hơn được.
/// </summary>
public sealed class QuotaThresholdWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private const int MaxTitleLength = 200;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<QuotaThresholdWorker> _logger;

    public QuotaThresholdWorker(IServiceProvider serviceProvider, ILogger<QuotaThresholdWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Job: Quota Threshold Worker is starting.");
        using var timer = new PeriodicTimer(PollInterval);

        do
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Một vòng lỗi (DB tạm mất kết nối…) không được giết worker: vòng sau thử lại.
                _logger.LogError(ex, "Unexpected error in quota threshold worker iteration");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Một vòng quét. <c>internal</c> để test gọi thẳng mà không phải chạy host — cùng lý do
    /// <see cref="Application.Schedules.ScheduledTaskRules"/> tách khỏi worker của nó.
    /// </summary>
    internal async Task RunOnceAsync(IServiceProvider scopedServices, CancellationToken ct)
    {
        var dbContext = scopedServices.GetRequiredService<HermesDbContext>();

        var monthStart = TenantQuotaUsage.MonthStartUtc(DateTime.UtcNow);
        var usedByTenant = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(dbContext, monthStart, ct);

        // Gói đang hiệu lực của từng đơn vị. Bất biến "một gói active mỗi đơn vị" được giữ ở tầng
        // ghi; dữ liệu cũ vi phạm thì dòng đến sau thắng, giống hệt khối hạn mức của dashboard.
        var subscriptions = await dbContext.Subscriptions.AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new { s.TenantId, PlanName = s.Plan!.Name, Limit = s.Plan!.MonthlyTokenLimit })
            .ToListAsync(ct);

        var planByTenant = new Dictionary<Guid, (string PlanName, int Limit)>();
        foreach (var subscription in subscriptions)
            planByTenant[subscription.TenantId] = (subscription.PlanName, subscription.Limit);

        var breaches = new List<(Guid TenantId, string PlanName, int Limit, int Used, string Type)>();
        foreach (var (tenantId, plan) in planByTenant)
        {
            var used = usedByTenant.TryGetValue(tenantId, out var value) ? value : 0;
            if (QuotaAlertRules.Classify(plan.Limit, used) is not { } type) continue;
            breaches.Add((tenantId, plan.PlanName, plan.Limit, used, type));
        }

        if (breaches.Count == 0) return;

        var breachTenantIds = breaches.Select(b => b.TenantId).Distinct().ToList();

        // Chống spam: mỗi đơn vị chỉ nhận MỘT thông báo cho mỗi (loại, tháng). Không chặn ở đây
        // thì mỗi vòng quét lại ném thêm một thông báo y hệt và người dùng sẽ tắt luôn tính năng.
        var alreadyNotified = (await dbContext.Notifications.AsNoTracking()
                .Where(n => breachTenantIds.Contains(n.TenantId)
                    && n.CreatedAtUtc >= monthStart
                    && (n.Type == QuotaAlertRules.ThresholdNotificationType
                        || n.Type == QuotaAlertRules.ExceededNotificationType))
                .Select(n => new { n.TenantId, n.Type })
                .ToListAsync(ct))
            .Select(n => (n.TenantId, n.Type))
            .ToHashSet();

        var pending = breaches.Where(b => !alreadyNotified.Contains((b.TenantId, b.Type))).ToList();
        if (pending.Count == 0) return;

        var pendingTenantIds = pending.Select(b => b.TenantId).Distinct().ToList();

        var tenantNames = await dbContext.Tenants.AsNoTracking()
            .Where(t => pendingTenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        // Người nhận là ADMIN của chính đơn vị: cảnh báo hạn mức chỉ hành động được bởi người có
        // quyền cấp thêm hạn mức/nâng gói, gửi cho Member là tạo thông báo không thể xử lý.
        var adminIdsByTenant = (await dbContext.Users.AsNoTracking()
                .Where(u => pendingTenantIds.Contains(u.TenantId) && u.Role == "Admin")
                .Select(u => new { u.Id, u.TenantId })
                .ToListAsync(ct))
            .GroupBy(u => u.TenantId)
            .ToDictionary(g => g.Key, g => g.Select(u => u.Id).ToList());

        var created = 0;
        foreach (var breach in pending)
        {
            if (!adminIdsByTenant.TryGetValue(breach.TenantId, out var adminIds) || adminIds.Count == 0)
            {
                _logger.LogWarning(
                    "Quota alert for tenant {TenantId} skipped: the tenant has no Admin to notify",
                    breach.TenantId);
                continue;
            }

            var tenantName = tenantNames.TryGetValue(breach.TenantId, out var name)
                ? name
                : breach.TenantId.ToString();
            var percent = QuotaAlertRules.Percent(breach.Used, breach.Limit);
            var title = Truncate(QuotaAlertRules.BuildTitle(breach.Type, percent), MaxTitleLength);
            var body = QuotaAlertRules.BuildBody(
                breach.Type, tenantName, breach.PlanName, breach.Used, breach.Limit, percent);

            foreach (var adminId in adminIds)
            {
                dbContext.Notifications.Add(new Notification
                {
                    TenantId = breach.TenantId,
                    UserId = adminId,
                    Type = breach.Type,
                    Title = title,
                    Body = body
                });
                created++;
            }

            _logger.LogInformation(
                "Quota alert {Type} for tenant {TenantId}: {Used}/{Limit} tokens ({Percent}%)",
                breach.Type, breach.TenantId, breach.Used, breach.Limit, percent);
        }

        if (created > 0) await dbContext.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
