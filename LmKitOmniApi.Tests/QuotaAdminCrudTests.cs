using LmKitOmniApi.Application.Quotas;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// CRUD hạn mức (gói / gán gói / grant / số dư). Đây là màn thay thế insert SQL tay, nên hàng rào
/// phải là CÁC BẤT BIẾN mà SQL tay trước đây không giữ hộ: một gói đang hoạt động cho mỗi đơn vị,
/// tên gói duy nhất, và không bao giờ xoá mất lịch sử đã tiêu.
/// </summary>
public sealed class QuotaAdminCrudTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    public QuotaAdminCrudTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Cục Trồng trọt" });
        db.Users.Add(new User
        {
            Id = _userId, TenantId = _tenantId, Username = "u", Email = "u@t.test",
            PasswordHash = "x", Role = "Member", FullName = "Người dùng"
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private static readonly CancellationToken None = CancellationToken.None;

    private Task<QuotaMutationResult> CreatePlanAsync(string? name, int limit, bool? isActive = null)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new CreatePlanCommand { Request = new SavePlanRequest(name, limit, isActive) }, None);

    private Task<QuotaMutationResult> UpdatePlanAsync(Guid id, string? name, int limit, bool? isActive = null)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new UpdatePlanCommand { Id = id, Request = new SavePlanRequest(name, limit, isActive) }, None);

    private Task<QuotaMutationResult> DeactivatePlanAsync(Guid id)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new DeactivatePlanCommand { Id = id }, None);

    private Task<QuotaMutationResult> AssignAsync(Guid tenantId, Guid planId, DateTime? renewal = null)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new AssignPlanCommand
            {
                TenantId = tenantId, PlanId = planId, RenewalAtUtc = renewal, ActorUserId = _actorId
            }, None);

    private Task<QuotaMutationResult> RemovePlanAsync(Guid tenantId)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new RemovePlanCommand { TenantId = tenantId }, None);

    private Task<QuotaMutationResult> CreateGrantAsync(Guid tenantId, int tokens, DateTime? expires = null, string? reason = null)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new CreateGrantCommand
            {
                TenantId = tenantId, Request = new SaveGrantRequest(tokens, expires, reason), ActorUserId = _actorId
            }, None);

    private Task<QuotaMutationResult> DeleteGrantAsync(Guid grantId)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new DeleteGrantCommand { GrantId = grantId }, None);

    private Task<QuotaMutationResult> SetCreditAsync(Guid tenantId, int balance)
        => new QuotaAdminCommandHandlers(NewContext())
            .Handle(new SetTenantCreditCommand { TenantId = tenantId, Balance = balance }, None);

    private Task<List<PlanDto>> ListPlansAsync()
        => new ListPlansQueryHandler(NewContext()).Handle(new ListPlansQuery(), None);

    private Task<List<TenantQuotaAdminDto>> ListTenantQuotasAsync()
        => new ListTenantQuotasQueryHandler(NewContext()).Handle(new ListTenantQuotasQuery(), None);

    private Task<List<GrantDto>> ListGrantsAsync(Guid tenantId)
        => new ListGrantsQueryHandler(NewContext()).Handle(new ListGrantsQuery { TenantId = tenantId }, None);

    private List<Subscription> Subscriptions()
    {
        using var db = NewContext();
        return db.Subscriptions.AsNoTracking().ToList();
    }

    private void SeedPlanRow(Guid id, string name, int limit, bool isActive = true)
    {
        using var db = NewContext();
        db.Plans.Add(new Plan { Id = id, Name = name, MonthlyTokenLimit = limit, IsActive = isActive });
        db.SaveChanges();
    }

    private void SeedUsage(int chatPrompt, int chatCompletion)
    {
        using var db = NewContext();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _userId, Title = "chat"
        };
        db.ChatSessions.Add(session);
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = session.Id, Role = "assistant", Content = "đáp", CreatedAt = DateTime.UtcNow,
            PromptTokens = chatPrompt, CompletionTokens = chatCompletion
        });
        db.SaveChanges();
    }

    // ------------------------------------------------------------------ Gói

    [Fact]
    public async Task CreatePlan_TrimsTheName_AndRejectsDuplicatesIgnoringCaseAndSpacing()
    {
        var created = await CreatePlanAsync("  Gói 1K  ", 1500);
        Assert.True(created.Success);
        Assert.NotNull(created.Id);

        using (var db = NewContext())
        {
            var plan = await db.Plans.SingleAsync(p => p.Id == created.Id);
            Assert.Equal("Gói 1K", plan.Name);
            Assert.Equal(1500, plan.MonthlyTokenLimit);
            Assert.True(plan.IsActive);
        }

        // Index unique của bảng chỉ chặn trùng tuyệt đối; "gói 1k" với khoảng trắng thừa vẫn phải
        // bị chặn ở tầng ứng dụng, nếu không admin tạo ra hai gói cho cùng một thứ.
        var duplicate = await CreatePlanAsync(" gói 1k ", 999);
        Assert.False(duplicate.Success);
        Assert.Equal("Tên gói đã tồn tại.", duplicate.Error);
    }

    [Fact]
    public async Task CreatePlan_RejectsEmptyName_AndNegativeLimit_ButAllowsZeroForUnlimited()
    {
        Assert.False((await CreatePlanAsync("   ", 100)).Success);
        Assert.False((await CreatePlanAsync("Gói âm", -1)).Success);

        // 0 = KHÔNG GIỚI HẠN (gói nội bộ) — không phải "gói 0 token".
        var unlimited = await CreatePlanAsync("Gói nội bộ", 0);
        Assert.True(unlimited.Success);
    }

    [Fact]
    public async Task UpdatePlan_CanRenameChangeLimitAndDeactivate()
    {
        var planId = (await CreatePlanAsync("Gói cũ", 100)).Id!.Value;

        var renamed = await UpdatePlanAsync(planId, "Gói mới", 2000, isActive: false);
        Assert.True(renamed.Success);

        var listed = Assert.Single(await ListPlansAsync());
        Assert.Equal("Gói mới", listed.Name);
        Assert.Equal(2000, listed.MonthlyTokenLimit);
        Assert.False(listed.IsActive);

        // Không gửi isActive thì giữ nguyên trạng thái hiện tại, không tự bật lại.
        await UpdatePlanAsync(planId, "Gói mới", 2500);
        Assert.False(Assert.Single(await ListPlansAsync()).IsActive);
    }

    [Fact]
    public async Task UpdatePlan_OnAMissingPlan_IsNotFound_NotAValidationError()
    {
        var result = await UpdatePlanAsync(Guid.NewGuid(), "Gói nào đó", 100);

        Assert.False(result.Success);
        // 404 vs 400 là hợp đồng với client: "không tìm thấy" phải phân biệt được với "sai dữ liệu".
        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task DeactivatePlan_RefusesWhileTenantsStillUseIt_ThenSucceedsAfterTheyMove()
    {
        var planId = (await CreatePlanAsync("Gói đang dùng", 1000)).Id!.Value;
        Assert.True((await AssignAsync(_tenantId, planId)).Success);

        var refused = await DeactivatePlanAsync(planId);
        Assert.False(refused.Success);
        Assert.Contains("1 đơn vị", refused.Error);
        Assert.True(Assert.Single(await ListPlansAsync()).IsActive);

        // Gỡ gói khỏi đơn vị rồi mới ngừng dùng được — admin không thể làm gói biến mất sau lưng
        // đơn vị đang dùng nó.
        Assert.True((await RemovePlanAsync(_tenantId)).Success);
        Assert.True((await DeactivatePlanAsync(planId)).Success);
        Assert.False(Assert.Single(await ListPlansAsync()).IsActive);

        // Và ngừng dùng lần hai là no-op thành công (idempotent), không ném.
        Assert.True((await DeactivatePlanAsync(planId)).Success);
    }

    // ------------------------------------------------------------------ Gán gói

    [Fact]
    public async Task AssignPlan_CreatesOneActiveSubscription_AndMovingPlansLeavesExactlyOne()
    {
        var first = (await CreatePlanAsync("Gói 1K", 1000)).Id!.Value;
        var second = (await CreatePlanAsync("Gói 2K", 2000)).Id!.Value;
        var renewal = DateTime.UtcNow.AddDays(30);

        var assigned = await AssignAsync(_tenantId, first, renewal);
        Assert.True(assigned.Success);

        var subscription = Assert.Single(Subscriptions());
        Assert.True(subscription.IsActive);
        Assert.Equal(first, subscription.PlanId);
        Assert.Equal(_actorId, subscription.CreatedByUserId);
        Assert.Equal(renewal, subscription.RenewalAtUtc);

        // Đổi gói: dòng cũ bị tắt, dòng mới bật — bất biến "một gói active mỗi đơn vị" giữ nguyên
        // nên dashboard không bao giờ cộng hạn mức hai lần.
        Assert.True((await AssignAsync(_tenantId, second)).Success);

        var all = Subscriptions();
        Assert.Equal(2, all.Count);
        var active = Assert.Single(all, s => s.IsActive);
        Assert.Equal(second, active.PlanId);
    }

    [Fact]
    public async Task AssignPlan_ToTheSamePlanAgain_UpdatesTheRenewalInsteadOfStackingRows()
    {
        var planId = (await CreatePlanAsync("Gói 1K", 1000)).Id!.Value;
        var firstRenewal = DateTime.UtcNow.AddDays(10);
        await AssignAsync(_tenantId, planId, firstRenewal);

        var secondRenewal = DateTime.UtcNow.AddDays(40);
        var again = await AssignAsync(_tenantId, planId, secondRenewal);
        Assert.True(again.Success);

        // Gia hạn cùng một gói không được tạo dòng mới: mã đơn vị sẽ mang hai bản ghi giống nhau
        // và mất dấu "dùng gói này từ bao giờ".
        var subscription = Assert.Single(Subscriptions());
        Assert.Equal(secondRenewal, subscription.RenewalAtUtc);
    }

    [Fact]
    public async Task AssignPlan_RejectsUnknownTenantPlanInactivePlanAndPastRenewal()
    {
        var planId = (await CreatePlanAsync("Gói 1K", 1000)).Id!.Value;
        var retired = (await CreatePlanAsync("Gói cũ", 500)).Id!.Value;
        Assert.True((await DeactivatePlanAsync(retired)).Success);

        var unknownTenant = await AssignAsync(Guid.NewGuid(), planId);
        Assert.False(unknownTenant.Success);
        Assert.True(unknownTenant.IsNotFound);

        var unknownPlan = await AssignAsync(_tenantId, Guid.NewGuid());
        Assert.False(unknownPlan.Success);
        Assert.True(unknownPlan.IsNotFound);

        var inactive = await AssignAsync(_tenantId, retired);
        Assert.False(inactive.Success);
        Assert.Contains("đã ngừng dùng", inactive.Error);

        // Kỳ gia hạn trong quá khứ là gán nhầm, không phải "đã hết hạn".
        var pastRenewal = await AssignAsync(_tenantId, planId, DateTime.UtcNow.AddDays(-1));
        Assert.False(pastRenewal.Success);

        Assert.Empty(Subscriptions());
    }

    [Fact]
    public async Task RemovePlan_IsIdempotent_AndLeavesTheTenantWithNoLimit()
    {
        var planId = (await CreatePlanAsync("Gói 1K", 1000)).Id!.Value;
        await AssignAsync(_tenantId, planId);

        Assert.True((await RemovePlanAsync(_tenantId)).Success);
        Assert.All(Subscriptions(), s => Assert.False(s.IsActive));

        // Lần hai: đơn vị vốn đã không còn gói, nên đây là trạng thái mong muốn chứ không phải lỗi.
        Assert.True((await RemovePlanAsync(_tenantId)).Success);

        var row = Assert.Single(await ListTenantQuotasAsync(), t => t.TenantId == _tenantId);
        Assert.Null(row.PlanId);
        Assert.True(row.IsUnlimited);
        Assert.Equal(0, row.UtilizationPct);
    }

    // ------------------------------------------------------------------ Grant

    [Fact]
    public async Task CreateGrant_StartsUnconsumed_AndValidatesInput()
    {
        var created = await CreateGrantAsync(_tenantId, 5000, DateTime.UtcNow.AddDays(7), "  bù hạn mức  ");
        Assert.True(created.Success);

        var grants = await ListGrantsAsync(_tenantId);
        var grant = Assert.Single(grants);
        Assert.Equal(5000, grant.Tokens);
        Assert.Equal(0, grant.UsedTokens);
        Assert.Equal(5000, grant.RemainingTokens);
        Assert.Equal("bù hạn mức", grant.Reason);
        Assert.False(grant.IsExpired);

        Assert.False((await CreateGrantAsync(_tenantId, 0)).Success);
        Assert.False((await CreateGrantAsync(_tenantId, -100)).Success);
        // Hạn trong quá khứ = grant chết ngay khi tạo; vô hạn thì để trống.
        Assert.False((await CreateGrantAsync(_tenantId, 100, DateTime.UtcNow.AddMinutes(-1))).Success);
        Assert.False((await CreateGrantAsync(_tenantId, 100, null, new string('x', 301))).Success);
        Assert.False((await CreateGrantAsync(Guid.NewGuid(), 100)).Success);
    }

    [Fact]
    public async Task DeleteGrant_RemovesAnUnusedGrant_ButNeverOneThatWasSpent()
    {
        var unused = (await CreateGrantAsync(_tenantId, 1000)).Id!.Value;
        Assert.True((await DeleteGrantAsync(unused)).Success);
        Assert.Empty(await ListGrantsAsync(_tenantId));

        var spent = (await CreateGrantAsync(_tenantId, 1000)).Id!.Value;
        using (var db = NewContext())
        {
            var grant = await db.TokenGrants.SingleAsync(g => g.Id == spent);
            grant.UsedTokens = 400;
            await db.SaveChangesAsync();
        }

        var refused = await DeleteGrantAsync(spent);
        Assert.False(refused.Success);
        Assert.Contains("400", refused.Error);
        Assert.Single(await ListGrantsAsync(_tenantId));

        Assert.True((await DeleteGrantAsync(Guid.NewGuid())).IsNotFound);
    }

    // ------------------------------------------------------------------ Số dư

    [Fact]
    public async Task SetCredit_CreatesTheRowOnce_ThenUpdatesIt()
    {
        Assert.True((await SetCreditAsync(_tenantId, 1500)).Success);
        // Lần thứ hai PHẢI là update: khoá unique trên TenantId làm insert thứ hai ném lỗi.
        Assert.True((await SetCreditAsync(_tenantId, 250)).Success);

        var row = Assert.Single(await ListTenantQuotasAsync(), t => t.TenantId == _tenantId);
        Assert.Equal(250, row.CreditBalance);

        Assert.False((await SetCreditAsync(_tenantId, -1)).Success);
        Assert.False((await SetCreditAsync(Guid.NewGuid(), 100)).Success);
    }

    // ------------------------------------------------------------------ Đọc trạng thái

    [Fact]
    public async Task TenantList_ReportsUsageFromChatAndAgentRuns_WithTheSamePercentAsTheAlert()
    {
        var planId = (await CreatePlanAsync("Gói 2K", 2000)).Id!.Value;
        await AssignAsync(_tenantId, planId, DateTime.UtcNow.AddDays(30));
        SeedUsage(chatPrompt: 400, chatCompletion: 200); // 600

        using (var db = NewContext())
        {
            var session = new ChatSession
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _userId, Title = "agent", IsAgentRun = true
            };
            db.ChatSessions.Add(session);
            db.AgentRuns.Add(new AgentRun
            {
                TenantId = _tenantId, UserId = _userId, ChatSessionId = session.Id, Goal = "việc",
                Status = "Completed", CreatedAtUtc = DateTime.UtcNow,
                PromptTokens = 900, CompletionTokens = 300, ModelName = "qwen3:8b"
            });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await ListTenantQuotasAsync(), t => t.TenantId == _tenantId);
        Assert.Equal("Gói 2K", row.PlanName);
        Assert.Equal(2000, row.MonthlyTokenLimit);
        Assert.False(row.IsUnlimited);
        // 600 chat + 1200 agent = 1800/2000 = 90%, và % này là CÙNG con số worker cảnh báo dùng.
        Assert.Equal(1800, row.UsedTokens);
        Assert.Equal(90, row.UtilizationPct);
        Assert.Equal(90, QuotaAlertRules.Percent(row.UsedTokens, row.MonthlyTokenLimit));
    }

    [Fact]
    public async Task GrantSummary_CountsOnlyGrantsThatAreStillUsableAndUnspent()
    {
        var planId = (await CreatePlanAsync("Gói 100", 100)).Id!.Value;
        await AssignAsync(_tenantId, planId);

        await CreateGrantAsync(_tenantId, 1000);                                  // còn hiệu lực
        await CreateGrantAsync(_tenantId, 500, DateTime.UtcNow.AddDays(3));        // còn hiệu lực
        var expired = (await CreateGrantAsync(_tenantId, 999, DateTime.UtcNow.AddDays(5))).Id!.Value;
        var spent = (await CreateGrantAsync(_tenantId, 700, null)).Id!.Value;

        using (var db = NewContext())
        {
            (await db.TokenGrants.SingleAsync(g => g.Id == expired)).ExpiresAtUtc = DateTime.UtcNow.AddDays(-1);
            (await db.TokenGrants.SingleAsync(g => g.Id == spent)).UsedTokens = 700; // tiêu hết
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await ListTenantQuotasAsync(), t => t.TenantId == _tenantId);
        // Grant hết hạn hoặc đã tiêu hết KHÔNG còn là hạn mức khả dụng — đếm nó là hứa hẹn token
        // không tồn tại.
        Assert.Equal(2, row.ActiveGrantCount);
        Assert.Equal(1500, row.GrantRemainingTokens);

        var all = await ListGrantsAsync(_tenantId);
        Assert.Equal(4, all.Count);
        Assert.True(all.Single(g => g.Id == expired).IsExpired);
    }

    [Fact]
    public async Task PlanList_TenantCountFollowsActiveSubscriptionsOnly()
    {
        var planId = (await CreatePlanAsync("Gói 1K", 1000)).Id!.Value;
        await AssignAsync(_tenantId, planId);
        Assert.Equal(1, Assert.Single(await ListPlansAsync()).TenantCount);

        // Gỡ gói: con số này phải về 0, nếu không admin sẽ tưởng gói vẫn còn người dùng và không
        // bao giờ dám ngừng dùng nó.
        await RemovePlanAsync(_tenantId);
        Assert.Equal(0, Assert.Single(await ListPlansAsync()).TenantCount);
    }

    [Fact]
    public async Task TenantList_CoversEveryTenant_EvenThoseWithoutAPlanOrAnyUsage()
    {
        SeedPlanRow(Guid.NewGuid(), "Gói mồ côi", 500);

        var rows = await ListTenantQuotasAsync();
        var row = Assert.Single(rows, t => t.TenantId == _tenantId);

        // Đơn vị chưa gán gói và chưa tiêu gì vẫn phải có dòng (nếu không thì không có cách nào
        // gán gói cho nó trên màn quản trị).
        Assert.True(row.IsUnlimited);
        Assert.Equal(0, row.UsedTokens);
        Assert.Equal(0, row.CreditBalance);
        Assert.Equal(0, row.ActiveGrantCount);
    }
}
