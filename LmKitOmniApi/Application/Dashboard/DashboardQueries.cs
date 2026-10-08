using System.Text;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Quotas;
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

        // Cua so cua AGENT-RUN trong ky. Day la nguon chi phi lon nhat ma bang chat khong thay
        // duoc: mot lan chay agent la nhieu luot suy luan noi tiep nhau, moi luot lai gui lai
        // tich luy. Dem theo CreatedAtUtc cua lan chay (luc BAT DAU) nen mot lan chay keo dai
        // qua nua dem van chi thuoc ve dung mot ky.
        var windowAgentRuns = _dbContext.AgentRuns.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc);

        cockpit.Tokens = await BuildTokensAsync(windowAssistant, windowAgentRuns, fromUtc, periodDays, cancellationToken);
        cockpit.Users = await BuildUsersAsync(windowAssistant, dto.TotalUsers, today, fromUtc, toUtc, periodDays, cancellationToken);
        cockpit.Spend = await BuildSpendAsync(windowAssistant, windowAgentRuns, cancellationToken);
        cockpit.Quota = await BuildQuotaAsync(cancellationToken);
        cockpit.Documents = await BuildDocumentsAsync(cancellationToken);
        cockpit.Activity = await BuildActivityAsync(fromUtc, toUtc, periodDays, cancellationToken);
        cockpit.Performance = await BuildPerformanceAsync(windowAssistant, windowAgentRuns, cancellationToken);
        cockpit.Alerts = await BuildAlertsAsync(cockpit.Quota, cancellationToken);

        dto.Cockpit = cockpit;
        return dto;
    }

    private async Task<DashboardTokensDto> BuildTokensAsync(
        IQueryable<Domain.Entities.ChatMessage> windowAssistant,
        IQueryable<Domain.Entities.AgentRun> windowAgentRuns,
        DateTime fromUtc,
        int periodDays,
        CancellationToken ct)
    {
        var tokens = new DashboardTokensDto
        {
            PromptTokens = await windowAssistant.SumAsync(m => (int?)m.PromptTokens, ct) ?? 0,
            CompletionTokens = await windowAssistant.SumAsync(m => (int?)m.CompletionTokens, ct) ?? 0,
            Messages = await windowAssistant.CountAsync(ct)
        };

        // Agent-run: cong tren cot rieng, va chi DEM nhung lan chay that su goi model (lan bi
        // hang doi tu choi de lai hai cot bang 0 -- dem no se thanh mot lan chay mien phi).
        tokens.AgentRunPromptTokens = await windowAgentRuns.SumAsync(r => (int?)r.PromptTokens, ct) ?? 0;
        tokens.AgentRunCompletionTokens = await windowAgentRuns.SumAsync(r => (int?)r.CompletionTokens, ct) ?? 0;
        tokens.AgentRuns = await windowAgentRuns
            .CountAsync(r => r.PromptTokens > 0 || r.CompletionTokens > 0, ct);

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
        IQueryable<Domain.Entities.ChatMessage> windowAssistant,
        IQueryable<Domain.Entities.AgentRun> windowAgentRuns,
        CancellationToken ct)
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

        var agentByTenant = await windowAgentRuns
            .Where(r => r.PromptTokens > 0 || r.CompletionTokens > 0)
            .GroupBy(r => r.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens),
                Runs = g.Count()
            })
            .ToListAsync(ct);

        var spend = new DashboardSpendDto();
        if (byTenant.Count == 0 && agentByTenant.Count == 0) return spend;

        // Mot dong cho moi don vi, ba cot agent-run de rieng: don vi nao chi chay agent (khong
        // chat) van phai xuat hien, neu khong thi phan chi phi lon nhat vo hinh tren bang.
        var rows = new Dictionary<Guid, TenantSpendRow>();
        foreach (var t in byTenant)
        {
            rows[t.TenantId] = new TenantSpendRow(t.TenantId, t.Prompt, t.Completion, t.Count, 0, 0, 0);
        }
        foreach (var a in agentByTenant)
        {
            rows.TryGetValue(a.TenantId, out var existing);
            var baseRow = existing ?? new TenantSpendRow(a.TenantId, 0, 0, 0, 0, 0, 0);
            rows[a.TenantId] = baseRow with
            {
                AgentRunPromptTokens = a.Prompt,
                AgentRunCompletionTokens = a.Completion,
                AgentRuns = a.Runs
            };
        }

        var tenantIds = rows.Keys.ToList();
        var names = await _dbContext.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        // Xep hang va ti le tap trung tinh tren TONG chi phi AI (chat + agent-run): mot don vi
        // dot token qua agent ma khong chat van la don vi dot nhieu nhat, va bo no ra khoi
        // "top 3 chiem bao nhieu %" se lam con so do sai lech theo huong lac quan.
        var ordered = rows.Values
            .OrderByDescending(t => t.ChatTokens + t.AgentTokens)
            .ThenBy(t => t.TenantId)
            .ToList();

        // Cộng bằng long rồi mới kẹp: tổng token của một kỳ dài có thể vượt int, và một tổng
        // bị tràn sẽ biến mọi tỉ lệ phần trăm phía dưới thành số vô nghĩa.
        var totalChatTokens = ordered.Sum(t => (long)t.ChatTokens);
        var totalAllTokens = ordered.Sum(t => (long)(t.ChatTokens + t.AgentTokens));
        spend.TotalTokens = (int)Math.Min(totalChatTokens, int.MaxValue);
        spend.TotalAgentRunTokens = (int)Math.Min(totalAllTokens - totalChatTokens, int.MaxValue);

        spend.ByTenant = ordered
            .Take(DashboardPeriod.TopLimit)
            .Select(t => new DashboardTenantUsageDto
            {
                TenantId = t.TenantId,
                TenantName = names.TryGetValue(t.TenantId, out var name) ? name : "(không xác định)",
                PromptTokens = t.Prompt,
                CompletionTokens = t.Completion,
                Messages = t.Count,
                AgentRunPromptTokens = t.AgentRunPromptTokens,
                AgentRunCompletionTokens = t.AgentRunCompletionTokens,
                AgentRuns = t.AgentRuns
            })
            .ToList();

        if (totalAllTokens > 0)
        {
            spend.Top3SharePct = Percent(ordered.Take(3).Sum(t => (long)(t.ChatTokens + t.AgentTokens)), totalAllTokens);
            spend.TopTenantSharePct = Percent(ordered[0].ChatTokens + ordered[0].AgentTokens, totalAllTokens);
        }
        spend.TopTenantName = spend.ByTenant.Count > 0 ? spend.ByTenant[0].TenantName : string.Empty;

        return spend;
    }

    /// <summary>
    /// Mot dong cua bang chi tieu theo don vi: chat va agent-run o cot rieng nhung doc ra tong thi
    /// cong ca hai. <see cref="ChatTokens"/>/<see cref="AgentTokens"/> khai bao mot lan de khong
    /// noi nao tu cong lai roi quen mot ve.
    /// </summary>
    private sealed record TenantSpendRow(
        Guid TenantId,
        int Prompt,
        int Completion,
        int Count,
        int AgentRunPromptTokens,
        int AgentRunCompletionTokens,
        int AgentRuns)
    {
        public int ChatTokens => Prompt + Completion;
        public int AgentTokens => AgentRunPromptTokens + AgentRunCompletionTokens;
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
        var monthStart = TenantQuotaUsage.MonthStartUtc(now);

        // "Đã dùng" đến từ TenantQuotaUsage — CÙNG nguồn với worker cảnh báo chủ động, và gồm CẢ
        // lượt chat lẫn lần chạy agent. Agent-run là nguồn đốt token lớn nhất; khi nó vô hình với
        // cột hạn mức thì cảnh báo 80% không bao giờ bắn đúng lúc.
        var usedByTenant = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(_dbContext, monthStart, ct);

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
        IQueryable<Domain.Entities.ChatMessage> windowAssistant,
        IQueryable<Domain.Entities.AgentRun> windowAgentRuns,
        CancellationToken ct)
    {
        // Chỉ lấy mẫu CÓ số đo: row cũ (0 ms) không phải "nhanh tức thì" mà là CHƯA ĐO, gộp
        // chúng vào trung bình sẽ kéo độ trễ xuống một cách giả tạo.
        var latencies = await windowAssistant
            .Where(m => m.LatencyMs > 0)
            .Select(m => m.LatencyMs)
            .ToListAsync(ct);

        // Cung ly do cho agent-run: lan chay truoc khi co tinh nang do do tre co LatencyMs = 0.
        var runLatencies = await windowAgentRuns
            .Where(r => r.LatencyMs > 0)
            .Select(r => r.LatencyMs)
            .ToListAsync(ct);

        var performance = new DashboardPerformanceDto();
        if (latencies.Count > 0)
        {
            latencies.Sort();
            performance.Samples = latencies.Count;
            performance.AvgLatencyMs = (int)Math.Round(latencies.Average());
            performance.P95LatencyMs = P95(latencies);
        }
        if (runLatencies.Count > 0)
        {
            runLatencies.Sort();
            performance.AgentRunSamples = runLatencies.Count;
            performance.AgentRunAvgLatencyMs = (int)Math.Round(runLatencies.Average());
            performance.AgentRunP95LatencyMs = P95(runLatencies);
        }
        return performance;
    }

    /// <summary>p95 = phan tu tai floor((n-1) * 0.95) cua danh sach DA sap xep tang dan.</summary>
    private static int P95(List<int> sortedLatencies)
        => sortedLatencies[(int)Math.Floor((sortedLatencies.Count - 1) * 0.95)];

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

        // Agent-run là nguồn chi phí lớn nhất và KHÔNG nằm trong bảng chat, nên bỏ nó khỏi báo
        // cáo là báo cáo nói thiếu. Cột riêng (không trộn vào cột chat) để người đọc biết tiền đi
        // đâu: cùng model, cùng đơn vị, nhưng một bên là lượt hỏi đáp và một bên là lần chạy tự
        // động nhiều bước.
        var agentRows = await _dbContext.AgentRuns.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc
                && (r.PromptTokens > 0 || r.CompletionTokens > 0))
            .GroupBy(r => new { r.TenantId, r.ModelName })
            .Select(g => new
            {
                g.Key.TenantId,
                g.Key.ModelName,
                Runs = g.Count(),
                Prompt = g.Sum(x => x.PromptTokens),
                Completion = g.Sum(x => x.CompletionTokens)
            })
            .ToListAsync(cancellationToken);

        // Gộp hai nguồn theo đúng một khóa (đơn vị × model): model chỉ xuất hiện ở một trong hai
        // nguồn vẫn phải có dòng, với cột của nguồn kia bằng 0.
        var merged = new Dictionary<(Guid TenantId, string Model), CsvUsageRow>();
        foreach (var row in rows)
        {
            var key = (row.TenantId, row.ModelName ?? "(unknown)");
            merged[key] = new CsvUsageRow(row.TenantId, key.Item2, row.Answers, row.Prompt, row.Completion, 0, 0, 0);
        }
        foreach (var row in agentRows)
        {
            var key = (row.TenantId, row.ModelName ?? "(unknown)");
            merged.TryGetValue(key, out var existing);
            var baseRow = existing ?? new CsvUsageRow(row.TenantId, key.Item2, 0, 0, 0, 0, 0, 0);
            merged[key] = baseRow with
            {
                AgentRuns = row.Runs,
                AgentPrompt = row.Prompt,
                AgentCompletion = row.Completion
            };
        }

        var tenantIds = merged.Keys.Select(k => k.TenantId).Distinct().ToList();
        var tenants = await _dbContext.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var csv = new StringBuilder();
        csv.Append('\uFEFF'); // BOM: Excel cần nó để đọc đúng tiếng Việt.
        // Ba cột agent-run nằm CUỐI: người đang đọc file theo 7 cột đầu không bị lệch.
        csv.Append("Mã đơn vị,Tên đơn vị,Model,Số câu trả lời,Prompt tokens,Completion tokens,Tổng token,"
            + "Số lần chạy agent,Prompt tokens agent,Completion tokens agent\r\n");

        foreach (var row in merged.Values
                     .OrderBy(r => tenants.TryGetValue(r.TenantId, out var n) ? n : string.Empty, StringComparer.Ordinal)
                     .ThenBy(r => r.Model, StringComparer.Ordinal))
        {
            var name = tenants.TryGetValue(row.TenantId, out var tenantName) ? tenantName : "(không xác định)";
            csv.Append(CsvField(row.TenantId.ToString())).Append(',')
                .Append(CsvField(name)).Append(',')
                .Append(CsvField(row.Model)).Append(',')
                .Append(row.Answers).Append(',')
                .Append(row.Prompt).Append(',')
                .Append(row.Completion).Append(',')
                .Append(row.Prompt + row.Completion).Append(',')
                .Append(row.AgentRuns).Append(',')
                .Append(row.AgentPrompt).Append(',')
                .Append(row.AgentCompletion).Append("\r\n");
        }

        var fileName = $"bao-cao-su-dung-{periodDays}ngay-{DateTime.UtcNow:yyyyMMdd}.csv";
        return new DashboardCsvResult(fileName, Encoding.UTF8.GetBytes(csv.ToString()));
    }

    /// <summary>Một dòng của file CSV: lượt chat và lượt agent-run của cùng (đơn vị × model).</summary>
    private sealed record CsvUsageRow(
        Guid TenantId,
        string Model,
        int Answers,
        int Prompt,
        int Completion,
        int AgentRuns,
        int AgentPrompt,
        int AgentCompletion);

    /// <summary>Bọc trường CSV trong dấu nháy kép khi có dấu phẩy/nháy/xuống dòng — tên đơn vị
    /// tiếng Việt hoàn toàn có thể chứa dấu phẩy và làm lệch mọi cột phía sau.</summary>
    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
