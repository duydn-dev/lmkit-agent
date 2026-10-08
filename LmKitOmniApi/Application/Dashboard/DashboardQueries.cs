using System.Text;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Dashboard;

/// <summary>
/// Tổng hợp toàn bộ số liệu vận hành cho dashboard quản trị.
///
/// Phân quyền nằm ở ĐÂY chứ không ở controller: <see cref="IsAdmin"/> quyết định có dựng khối
/// <c>cockpit</c> hay không, còn <c>myUsage</c> luôn được tính cho chính người gọi. Nhờ vậy
/// không có nhánh nào trả về số liệu hệ thống cho Member, kể cả khi controller bị sửa sai.
///
/// Số token là ƯỚC LƯỢNG ghi lúc lưu row assistant (xem <c>ChatMessage</c>); row cũ trước khi
/// có tính năng này mang token 0 nên vẫn được đếm là "lượt trả lời" nhưng không đóng góp token.
/// </summary>
public sealed class GetDashboardStatsQuery : IRequest<DashboardStatsDto>
{
    /// <summary>Tenant của người gọi — dùng cho khối hoạt động và usage cá nhân.</summary>
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>True khi caller là Admin: nhận toàn bộ số liệu mọi đơn vị.</summary>
    public bool IsAdmin { get; set; }

    public int PeriodDays { get; set; } = DashboardPeriod.DefaultDays;
}

/// <summary>Báo cáo CSV (đơn vị × model) — chỉ Admin gọi được.</summary>
public sealed class GetDashboardCsvQuery : IRequest<DashboardCsvResult>
{
    public int PeriodDays { get; set; } = DashboardPeriod.DefaultDays;
}

public sealed record DashboardCsvResult(string FileName, byte[] Content);

public sealed class GetDashboardStatsQueryHandler : IRequestHandler<GetDashboardStatsQuery, DashboardStatsDto>
{
    private readonly HermesDbContext _dbContext;

    public GetDashboardStatsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<DashboardStatsDto> Handle(GetDashboardStatsQuery request, CancellationToken cancellationToken)
    {
        var periodDays = DashboardPeriod.Normalize(request.PeriodDays);
        var today = DateTime.UtcNow.Date;
        var fromUtc = today.AddDays(-(periodDays - 1));
        var toUtc = today.AddDays(1);

        var dto = new DashboardStatsDto
        {
            PeriodDays = periodDays,
            TotalUsers = await _dbContext.Users.CountAsync(cancellationToken),
            TotalTenants = await _dbContext.Tenants.CountAsync(cancellationToken),
            TotalSessions = await _dbContext.ChatSessions.CountAsync(cancellationToken),
            TotalDocuments = await _dbContext.Documents.CountAsync(cancellationToken),
            MyUsage = await ComputeSelfAsync(request.UserId, fromUtc, toUtc, cancellationToken)
        };

        // Member không bao giờ nhận khối cockpit — không dựng thì không thể lộ.
        if (!request.IsAdmin) return dto;

        var cockpit = new DashboardCockpitDto { PeriodDays = periodDays };

        // Lượt trả lời trong kỳ: nguồn của MỌI số liệu token. Chỉ row assistant được tính để
        // một hội thoại không bị tính chi phí hai lần (row user luôn có token 0).
        var windowAssistant = _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "assistant" && m.CreatedAt >= fromUtc && m.CreatedAt < toUtc);

        cockpit.Tokens = await BuildTokensAsync(windowAssistant, fromUtc, periodDays, cancellationToken);
        cockpit.Users = await BuildUsersAsync(windowAssistant, dto.TotalUsers, today, fromUtc, toUtc, periodDays, cancellationToken);
        cockpit.Spend = await BuildSpendAsync(windowAssistant, cancellationToken);
        cockpit.Quota = await BuildQuotaAsync(cancellationToken);
        cockpit.Documents = await BuildDocumentsAsync(cancellationToken);
        cockpit.Activity = await BuildActivityAsync(fromUtc, toUtc, periodDays, cancellationToken);
        cockpit.Performance = await BuildPerformanceAsync(windowAssistant, cancellationToken);
        cockpit.Alerts = await BuildAlertsAsync(cockpit.Quota, cancellationToken);

        dto.Cockpit = cockpit;
        return dto;
    }

    private async Task<DashboardTokensDto> BuildTokensAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant, DateTime fromUtc, int periodDays, CancellationToken ct)
    {
        var tokens = new DashboardTokensDto
        {
            PromptTokens = await windowAssistant.SumAsync(m => (int?)m.PromptTokens, ct) ?? 0,
            CompletionTokens = await windowAssistant.SumAsync(m => (int?)m.CompletionTokens, ct) ?? 0,
            Messages = await windowAssistant.CountAsync(ct)
        };

        // Gom theo năm/tháng/ngày thay vì <c>CreatedAt.Date</c>: Npgsql dịch .Date trên cột
        // timestamptz thành phép cast phụ thuộc timezone phiên, còn date_part là xác định.
        var daily = await windowAssistant
            .GroupBy(m => new { m.CreatedAt.Year, m.CreatedAt.Month, m.CreatedAt.Day })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens),
                Count = g.Count()
            })
            .ToListAsync(ct);

        var byDay = daily.ToDictionary(
            d => new DateTime(d.Year, d.Month, d.Day),
            d => new DashboardDailyTokenDto { PromptTokens = d.Prompt, CompletionTokens = d.Completion, Messages = d.Count });

        for (var i = 0; i < periodDays; i++)
        {
            var day = fromUtc.AddDays(i);
            if (byDay.TryGetValue(day, out var row))
            {
                row.Date = day.ToString("yyyy-MM-dd");
            }
            else
            {
                row = new DashboardDailyTokenDto { Date = day.ToString("yyyy-MM-dd") };
            }
            tokens.Daily.Add(row);
        }

        tokens.ByModel = (await windowAssistant
                .GroupBy(m => m.ModelName)
                .Select(g => new DashboardModelUsageDto
                {
                    // Row sinh trước khi có tính năng đếm token không có model; gộp chúng lại
                    // thành một nhóm "(unknown)" thay vì rải rác nhiều nhóm null.
                    ModelName = g.Key ?? "(unknown)",
                    PromptTokens = g.Sum(x => x.PromptTokens),
                    CompletionTokens = g.Sum(x => x.CompletionTokens),
                    Messages = g.Count()
                })
                .ToListAsync(ct))
            .OrderByDescending(m => m.PromptTokens + m.CompletionTokens)
            .ThenBy(m => m.ModelName, StringComparer.Ordinal)
            .ToList();

        return tokens;
    }

    private async Task<DashboardUsersDto> BuildUsersAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant, int totalUsers, DateTime today, DateTime fromUtc, DateTime toUtc,
        int periodDays, CancellationToken ct)
    {
        // DAU/WAU/MAU cố định 1/7/30 ngày bất kể cửa sổ đang chọn. Phiên agent-run và phiên
        // tạm thời bị loại: chúng không phải hoạt động chat thật của người dùng.
        var activeSessions = _dbContext.ChatSessions.AsNoTracking()
            .Where(s => s.UserId != null && !s.IsAgentRun && !s.IsEphemeral);

        var users = new DashboardUsersDto
        {
            TotalUsers = totalUsers,
            DailyActive = await DistinctActorsAsync(activeSessions, today, ct),
            WeeklyActive = await DistinctActorsAsync(activeSessions, today.AddDays(-6), ct),
            MonthlyActive = await DistinctActorsAsync(activeSessions, today.AddDays(-29), ct),
            NewUsers = await _dbContext.Users.CountAsync(u => u.CreatedAt >= fromUtc, ct)
        };
        users.AdoptionPct = totalUsers > 0 ? (int)Math.Round(users.MonthlyActive * 100d / totalUsers) : 0;

        // Đếm người dùng hoạt động mỗi ngày: lấy (ngày, user) rồi DISTINCT trong bộ nhớ. Số
        // phiên trong kỳ nhỏ hơn số message rất nhiều nên cách này rẻ và không phụ thuộc việc
        // provider có dịch được COUNT(DISTINCT) trong GROUP BY hay không.
        var pairs = await activeSessions
            .Where(s => s.CreatedAt >= fromUtc && s.CreatedAt < toUtc)
            .Select(s => new { s.CreatedAt, s.UserId })
            .ToListAsync(ct);

        var perDay = pairs
            .GroupBy(p => p.CreatedAt.Date)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.UserId).Distinct().Count());

        for (var i = 0; i < periodDays; i++)
        {
            var day = fromUtc.AddDays(i);
            users.Daily.Add(new DashboardDailyCountDto
            {
                Date = day.ToString("yyyy-MM-dd"),
                Count = perDay.TryGetValue(day, out var count) ? count : 0
            });
        }

        users.TopUsers = await BuildTopUsersAsync(windowAssistant, fromUtc, toUtc, ct);
        return users;
    }

    private static Task<int> DistinctActorsAsync(
        IQueryable<Domain.Entities.ChatSession> activeSessions, DateTime since, CancellationToken ct)
        => activeSessions
            .Where(s => s.CreatedAt >= since)
            .Select(s => s.UserId!.Value)
            .Distinct()
            .CountAsync(ct);

    private async Task<List<DashboardTopUserDto>> BuildTopUsersAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var answersByUser = await windowAssistant
            .Where(m => m.ChatSession!.UserId != null)
            .GroupBy(m => m.ChatSession!.UserId!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                Answers = g.Count(),
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens)
            })
            .ToListAsync(ct);

        var questionsByUser = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "user" && m.CreatedAt >= fromUtc && m.CreatedAt < toUtc && m.ChatSession!.UserId != null)
            .GroupBy(m => m.ChatSession!.UserId!.Value)
            .Select(g => new { UserId = g.Key, Questions = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Questions, ct);

        var ranked = answersByUser
            .OrderByDescending(a => a.Prompt + a.Completion)
            .ThenBy(a => a.UserId)
            .Take(DashboardPeriod.TopLimit)
            .ToList();

        // Tra tên trong MỘT truy vấn cho cả bảng xếp hạng, và chỉ dựng dòng khi người dùng
        // còn tồn tại: một session trỏ tới user đã bị xoá cứng (dữ liệu di trú tay) sẽ hiện
        // thành "(unknown) 0 token" nếu tin tưởng mù quáng vào khóa ngoại.
        var ids = ranked.Select(r => r.UserId).ToList();
        var identities = await _dbContext.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email })
            .ToDictionaryAsync(u => u.Id, ct);

        var result = new List<DashboardTopUserDto>(ranked.Count);
        foreach (var row in ranked)
        {
            if (!identities.TryGetValue(row.UserId, out var user)) continue;
            result.Add(new DashboardTopUserDto
            {
                UserId = row.UserId,
                Name = user.FullName,
                Email = user.Email,
                Questions = questionsByUser.TryGetValue(row.UserId, out var q) ? q : 0,
                Answers = row.Answers,
                PromptTokens = row.Prompt,
                CompletionTokens = row.Completion
            });
        }
        return result;
    }

    private async Task<DashboardSpendDto> BuildSpendAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant, CancellationToken ct)
    {
        var byTenant = await windowAssistant
            .GroupBy(m => m.ChatSession!.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens),
                Count = g.Count()
            })
            .ToListAsync(ct);

        var spend = new DashboardSpendDto();
        if (byTenant.Count == 0) return spend;

        var tenantIds = byTenant.Select(t => t.TenantId).ToList();
        var names = await _dbContext.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var ordered = byTenant
            .OrderByDescending(t => t.Prompt + t.Completion)
            .ThenBy(t => t.TenantId)
            .ToList();

        // Cộng bằng long rồi mới kẹp: tổng token của một kỳ dài có thể vượt int, và một tổng
        // bị tràn sẽ biến mọi tỉ lệ phần trăm phía dưới thành số vô nghĩa.
        var totalTokens = ordered.Sum(t => (long)(t.Prompt + t.Completion));
        spend.TotalTokens = (int)Math.Min(totalTokens, int.MaxValue);

        spend.ByTenant = ordered
            .Take(DashboardPeriod.TopLimit)
            .Select(t => new DashboardTenantUsageDto
            {
                TenantId = t.TenantId,
                TenantName = names.TryGetValue(t.TenantId, out var name) ? name : "(không xác định)",
                PromptTokens = t.Prompt,
                CompletionTokens = t.Completion,
                Messages = t.Count
            })
            .ToList();

        if (totalTokens > 0)
        {
            spend.Top3SharePct = Percent(ordered.Take(3).Sum(t => (long)(t.Prompt + t.Completion)), totalTokens);
            spend.TopTenantSharePct = Percent(ordered[0].Prompt + ordered[0].Completion, totalTokens);
        }
        spend.TopTenantName = spend.ByTenant.Count > 0 ? spend.ByTenant[0].TenantName : string.Empty;

        return spend;
    }

    private async Task<DashboardQuotaDto> BuildQuotaAsync(CancellationToken ct)
    {
        var quota = new DashboardQuotaDto();

        var tenants = await _dbContext.Tenants.AsNoTracking()
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        // Hạn mức gói reset theo tháng nên "đã dùng" phải là tháng DƯƠNG LỊCH hiện tại, không
        // phải cửa sổ 7/30/90 của dashboard — nếu lấy theo cửa sổ thì đổi bộ lọc sẽ làm % hạn
        // mức nhảy múa dù hạn mức không hề thay đổi.
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var usedByTenant = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "assistant" && m.CreatedAt >= monthStart)
            .GroupBy(m => m.ChatSession!.TenantId)
            .Select(g => new { TenantId = g.Key, Used = g.Sum(x => x.PromptTokens + x.CompletionTokens) })
            .ToDictionaryAsync(x => x.TenantId, x => x.Used, ct);

        var subscriptions = await _dbContext.Subscriptions.AsNoTracking()
            .Where(s => s.IsActive)
            .Select(s => new
            {
                s.TenantId,
                s.RenewalAtUtc,
                PlanName = s.Plan!.Name,
                PlanLimit = s.Plan!.MonthlyTokenLimit
            })
            .ToListAsync(ct);

        var credits = await _dbContext.TenantCredits.AsNoTracking()
            .ToDictionaryAsync(c => c.TenantId, c => c.Balance, ct);

        var planByTenant = new Dictionary<Guid, (string Name, int Limit, DateTime? Renewal)>();
        foreach (var sub in subscriptions)
        {
            // Bất biến "một gói active mỗi đơn vị" được giữ ở tầng ghi; nếu dữ liệu cũ vi phạm,
            // dòng đến sau thắng thay vì ném lỗi và làm hỏng cả dashboard.
            planByTenant[sub.TenantId] = (sub.PlanName, sub.PlanLimit, sub.RenewalAtUtc);
        }

        foreach (var tenant in tenants.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var hasPlan = planByTenant.TryGetValue(tenant.Id, out var plan);
            var limit = hasPlan ? plan.Limit : 0;
            var isUnlimited = !hasPlan || limit <= 0;
            var used = usedByTenant.TryGetValue(tenant.Id, out var u) ? u : 0;

            var row = new DashboardTenantQuotaDto
            {
                TenantId = tenant.Id,
                TenantName = tenant.Name,
                PlanName = hasPlan ? plan.Name : null,
                MonthlyLimit = isUnlimited ? 0 : limit,
                UsedTokens = used,
                UtilizationPct = isUnlimited ? 0 : Percent(used, limit),
                CreditBalance = credits.TryGetValue(tenant.Id, out var balance) ? balance : 0,
                RenewalAtUtc = hasPlan ? plan.Renewal : null,
                IsUnlimited = isUnlimited
            };
            quota.ByTenant.Add(row);

            if (!hasPlan)
            {
                quota.TenantsWithoutPlan++;
                continue;
            }

            quota.TenantsOnPlan++;
            if (isUnlimited) continue;

            quota.TotalMonthlyLimit += limit;
            quota.TotalMonthlyUsed += used;

            if (row.UtilizationPct >= 100) quota.TenantsOverLimit++;
            else if (row.UtilizationPct >= DashboardPeriod.OverThresholdPercent) quota.TenantsOverThreshold++;
        }

        // Đơn vị có gói active nhưng không nằm trong bảng tenants (dữ liệu mồ côi) vẫn phải
        // được tính vào hạn mức tổng, nếu không con số tổng sẽ nhỏ hơn tổng các phần.
        var tenantIds = tenants.Select(t => t.Id).ToHashSet();
        foreach (var orphan in subscriptions
                     .Where(s => !tenantIds.Contains(s.TenantId))
                     .GroupBy(s => s.TenantId)
                     .Select(g => g.First()))
        {
            quota.TenantsOnPlan++;
            if (orphan.PlanLimit > 0)
            {
                quota.TotalMonthlyLimit += orphan.PlanLimit;
                quota.TotalMonthlyUsed += usedByTenant.TryGetValue(orphan.TenantId, out var orphanUsed) ? orphanUsed : 0;
            }
        }

        quota.TotalCreditBalance = credits.Values.Sum();
        return quota;
    }

    /// <summary>
    /// Cảnh báo "cần chú ý". Hai nguồn khác nhau và KHÔNG được trộn: mức dùng hạn mức (từ khối
    /// quota) và grant cấp thêm sắp hết hạn (truy vấn riêng token_grants). Trước đây gộp chung
    /// sẽ khiến một đơn vị vừa gần đầy hạn mức vừa có grant bị đếm/liệt kê sai bản chất.
    /// </summary>
    private async Task<DashboardAlertsDto> BuildAlertsAsync(DashboardQuotaDto quota, CancellationToken ct)
    {
        var alerts = new DashboardAlertsDto
        {
            NearLimitTenants = quota.TenantsOverThreshold,
            OverLimitTenants = quota.TenantsOverLimit
        };

        // Chỉ grant CÒN token và CÓ hạn: ExpiresAtUtc null nghĩa là vô hạn, không bao giờ "sắp
        // hết hạn" — xếp chúng vào đây sẽ tạo cảnh báo vĩnh viễn không thể dập.
        var horizon = DateTime.UtcNow.AddDays(DashboardPeriod.GrantExpiryHorizonDays);
        var expiring = await _dbContext.TokenGrants.AsNoTracking()
            .Where(g => g.ExpiresAtUtc != null && g.ExpiresAtUtc <= horizon && g.Tokens - g.UsedTokens > 0)
            .Select(g => new { g.TenantId, g.Tokens, g.UsedTokens, g.ExpiresAtUtc })
            .ToListAsync(ct);

        alerts.ExpiringGrantCount = expiring.Count;

        var tenantIds = expiring.Select(g => g.TenantId).Distinct().ToList();
        var names = await _dbContext.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        alerts.ExpiringGrants = expiring
            .OrderBy(g => g.ExpiresAtUtc)
            .Take(DashboardPeriod.TopLimit)
            .Select(g => new DashboardExpiringGrantDto
            {
                TenantId = g.TenantId,
                TenantName = names.TryGetValue(g.TenantId, out var name) ? name : "(không xác định)",
                RemainingTokens = g.Tokens - g.UsedTokens,
                ExpiresAtUtc = g.ExpiresAtUtc!.Value,
                DaysLeft = Math.Max(0, (int)Math.Ceiling((g.ExpiresAtUtc!.Value - DateTime.UtcNow).TotalDays))
            })
            .ToList();

        return alerts;
    }

    private async Task<DashboardDocumentsDto> BuildDocumentsAsync(CancellationToken ct)
    {
        var documents = _dbContext.Documents.AsNoTracking();

        var byStatus = await documents
            .GroupBy(d => d.VectorizationStatus)
            .Select(g => new DashboardCountDto { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var bySource = await documents
            .GroupBy(d => d.Source)
            .Select(g => new DashboardCountDto { Key = g.Key ?? "(không rõ)", Count = g.Count() })
            .ToListAsync(ct);

        return new DashboardDocumentsDto
        {
            Total = await documents.CountAsync(ct),
            Indexed = await documents.CountAsync(d => d.VectorizationStatus == Domain.Entities.Document.CompletedStatus, ct),
            Pending = await documents.CountAsync(
                d => d.VectorizationStatus == Domain.Entities.Document.PendingStatus
                     || d.VectorizationStatus == Domain.Entities.Document.ProcessingStatus, ct),
            Failed = await documents.CountAsync(d => d.VectorizationStatus == Domain.Entities.Document.FailedStatus, ct),
            TotalChunks = await _dbContext.DocumentChunks.CountAsync(ct),
            ByStatus = byStatus.OrderByDescending(s => s.Count).ToList(),
            BySource = bySource.OrderByDescending(s => s.Count).ToList()
        };
    }

    private async Task<DashboardActivityDto> BuildActivityAsync(
        DateTime fromUtc, DateTime toUtc, int periodDays, CancellationToken ct)
    {
        var logs = _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= fromUtc && a.CreatedAtUtc < toUtc);

        var daily = await logs
            .GroupBy(a => new { a.CreatedAtUtc.Year, a.CreatedAtUtc.Month, a.CreatedAtUtc.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(ct);

        var byDay = daily.ToDictionary(
            d => new DateTime(d.Year, d.Month, d.Day),
            d => d.Count);

        var activity = new DashboardActivityDto { Total = await logs.CountAsync(ct) };
        for (var i = 0; i < periodDays; i++)
        {
            var day = fromUtc.AddDays(i);
            activity.Daily.Add(new DashboardDailyCountDto
            {
                Date = day.ToString("yyyy-MM-dd"),
                Count = byDay.TryGetValue(day, out var count) ? count : 0
            });
        }

        activity.TopActions = await logs
            .GroupBy(a => a.Action)
            .Select(g => new DashboardCountDto { Key = g.Key, Count = g.Count() })
            .OrderByDescending(a => a.Count)
            .Take(DashboardPeriod.TopLimit)
            .ToListAsync(ct);

        return activity;
    }

    private static async Task<DashboardPerformanceDto> BuildPerformanceAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant, CancellationToken ct)
    {
        // Chỉ lấy mẫu CÓ số đo: row cũ (0 ms) không phải "nhanh tức thì" mà là CHƯA ĐO, gộp
        // chúng vào trung bình sẽ kéo độ trễ xuống một cách giả tạo.
        var latencies = await windowAssistant
            .Where(m => m.LatencyMs > 0)
            .Select(m => m.LatencyMs)
            .ToListAsync(ct);

        if (latencies.Count == 0) return new DashboardPerformanceDto();

        latencies.Sort();
        return new DashboardPerformanceDto
        {
            Samples = latencies.Count,
            AvgLatencyMs = (int)Math.Round(latencies.Average()),
            P95LatencyMs = latencies[(int)Math.Floor((latencies.Count - 1) * 0.95)]
        };
    }

    private async Task<DashboardSelfDto> ComputeSelfAsync(Guid userId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var self = new DashboardSelfDto();

        var myAssistant = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "assistant" && m.CreatedAt >= fromUtc && m.CreatedAt < toUtc
                        && m.ChatSession!.UserId == userId)
            .GroupBy(m => new { m.CreatedAt.Year, m.CreatedAt.Month, m.CreatedAt.Day })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens),
                Count = g.Count()
            })
            .ToListAsync(ct);

        self.Answers = myAssistant.Sum(a => a.Count);
        self.PromptTokens = myAssistant.Sum(a => a.Prompt);
        self.CompletionTokens = myAssistant.Sum(a => a.Completion);

        self.Questions = await _dbContext.ChatMessages.AsNoTracking()
            .CountAsync(m => m.Role == "user" && m.CreatedAt >= fromUtc && m.CreatedAt < toUtc
                             && m.ChatSession!.UserId == userId, ct);

        self.Sessions = await _dbContext.ChatSessions.AsNoTracking()
            .CountAsync(s => s.UserId == userId && s.CreatedAt >= fromUtc && s.CreatedAt < toUtc
                             && !s.IsAgentRun && !s.IsEphemeral, ct);

        var byDay = myAssistant.ToDictionary(a => new DateTime(a.Year, a.Month, a.Day), a => a.Count);
        for (var day = fromUtc; day < toUtc; day = day.AddDays(1))
        {
            self.Daily.Add(new DashboardDailyCountDto
            {
                Date = day.ToString("yyyy-MM-dd"),
                Count = byDay.TryGetValue(day, out var count) ? count : 0
            });
        }

        return self;
    }

    /// <summary>Phần trăm, làm tròn, có thể &gt; 100 khi vượt hạn mức.</summary>
    private static int Percent(long value, long total)
        => total <= 0 ? 0 : (int)Math.Round(value * 100d / total);
}

public sealed class GetDashboardCsvQueryHandler : IRequestHandler<GetDashboardCsvQuery, DashboardCsvResult>
{
    private readonly HermesDbContext _dbContext;

    public GetDashboardCsvQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<DashboardCsvResult> Handle(GetDashboardCsvQuery request, CancellationToken cancellationToken)
    {
        var periodDays = DashboardPeriod.Normalize(request.PeriodDays);
        var today = DateTime.UtcNow.Date;
        var fromUtc = today.AddDays(-(periodDays - 1));
        var toUtc = today.AddDays(1);

        var rows = await _dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "assistant" && m.CreatedAt >= fromUtc && m.CreatedAt < toUtc)
            .GroupBy(m => new { m.ChatSession!.TenantId, m.ModelName })
            .Select(g => new
            {
                g.Key.TenantId,
                g.Key.ModelName,
                Answers = g.Count(),
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens)
            })
            .ToListAsync(cancellationToken);

        var tenantIds = rows.Select(r => r.TenantId).Distinct().ToList();
        var tenants = await _dbContext.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var csv = new StringBuilder();
        csv.Append('\uFEFF'); // BOM: Excel cần nó để đọc đúng tiếng Việt.
        csv.Append("Mã đơn vị,Tên đơn vị,Model,Số câu trả lời,Prompt tokens,Completion tokens,Tổng token\r\n");

        foreach (var row in rows
                     .OrderBy(r => tenants.TryGetValue(r.TenantId, out var n) ? n : string.Empty, StringComparer.Ordinal)
                     .ThenBy(r => r.ModelName ?? string.Empty, StringComparer.Ordinal))
        {
            var name = tenants.TryGetValue(row.TenantId, out var tenantName) ? tenantName : "(không xác định)";
            csv.Append(CsvField(row.TenantId.ToString())).Append(',')
                .Append(CsvField(name)).Append(',')
                .Append(CsvField(row.ModelName ?? "(unknown)")).Append(',')
                .Append(row.Answers).Append(',')
                .Append(row.Prompt).Append(',')
                .Append(row.Completion).Append(',')
                .Append(row.Prompt + row.Completion).Append("\r\n");
        }

        var fileName = $"bao-cao-su-dung-{periodDays}ngay-{DateTime.UtcNow:yyyyMMdd}.csv";
        return new DashboardCsvResult(fileName, Encoding.UTF8.GetBytes(csv.ToString()));
    }

    /// <summary>Bọc trường CSV trong dấu nháy kép khi có dấu phẩy/nháy/xuống dòng — tên đơn vị
    /// tiếng Việt hoàn toàn có thể chứa dấu phẩy và làm lệch mọi cột phía sau.</summary>
    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
