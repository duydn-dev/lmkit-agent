using LmKitOmniApi.Application.Dashboard;
using LmKitOmniApi.Application.Quotas;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Luật cảnh báo hạn mức là logic thuần: ngưỡng phải là CON SỐ CỦA DASHBOARD
/// (<see cref="DashboardPeriod.OverThresholdPercent"/>), không phải một bản sao thứ hai. Test ở
/// đây ghim đúng biên 80%/100% để lệch ngưỡng bị bắt ngay trong CI thay vì lúc khách hàng đã
/// vượt hạn mức mà không nhận được thông báo nào.
/// </summary>
public sealed class QuotaAlertRulesTests
{
    [Theory]
    // Gói không giới hạn: không có trần thì không có "gần chạm trần" — bắn ở đây là báo động giả
    // vĩnh viễn không thể dập.
    [InlineData(0, 999_999, null)]
    [InlineData(-1, 500, null)]
    // Biên dưới: 79% im lặng, 80% bắt đầu cảnh báo (>=, không phải >).
    [InlineData(1000, 794, null)]
    [InlineData(1000, 800, QuotaAlertRules.ThresholdNotificationType)]
    [InlineData(1000, 990, QuotaAlertRules.ThresholdNotificationType)]
    // Chạm trần đúng bằng hạn mức đã là vượt, và phải là loại cảnh báo NẶNG hơn.
    [InlineData(1000, 1000, QuotaAlertRules.ExceededNotificationType)]
    [InlineData(1000, 2500, QuotaAlertRules.ExceededNotificationType)]
    [InlineData(1500, 1200, QuotaAlertRules.ThresholdNotificationType)]
    public void Classify_UsesTheDashboardThreshold(int limit, int used, string? expected)
        => Assert.Equal(expected, QuotaAlertRules.Classify(limit, used));

    [Fact]
    public void Classify_DecidesOnTheRoundedPercent_SoTheAlertFiresExactlyWhenTheDashboardFlagsIt()
    {
        // 799/1000 = 79,9% -> dashboard hiển thị 80% và tự tính đơn vị này vào "đã chạm ngưỡng"
        // (khối hạn mức cũng so trên số ĐÃ LÀM TRÒN). Thông báo phải bắn đúng lúc đó; nếu so trên
        // số thô thì màn hình nói "80%" mà không có thông báo nào — người dùng chỉ phát hiện khi
        // đã muộn.
        Assert.Equal(80, QuotaAlertRules.Percent(799, 1000));
        Assert.Equal(QuotaAlertRules.ThresholdNotificationType, QuotaAlertRules.Classify(1000, 799));

        // 999/1000 = 99,9% -> làm tròn thành 100%, và dashboard cũng xếp nó vào NHÓM VƯỢT hạn mức
        // (ngưỡng 100 dùng cùng một con số). Hai bên không được lệch nhau ở đúng ranh giới này.
        Assert.Equal(100, QuotaAlertRules.Percent(999, 1000));
        Assert.Equal(QuotaAlertRules.ExceededNotificationType, QuotaAlertRules.Classify(1000, 999));
    }

    [Fact]
    public void Percent_RoundsAndNeverDividesByZero()
    {
        Assert.Equal(80, QuotaAlertRules.Percent(800, 1000));
        // 2/3 -> 67, làm tròn chứ không cắt: ngưỡng so bằng số đã làm tròn nên đây là định nghĩa
        // duy nhất, không thể lệch với con số hiển thị trên dashboard.
        Assert.Equal(67, QuotaAlertRules.Percent(2, 3));
        Assert.Equal(137, QuotaAlertRules.Percent(2050, 1500));
        Assert.Equal(0, QuotaAlertRules.Percent(500, 0));
    }

    [Fact]
    public void Titles_AndBodies_StateWhichOfTheTwoThingsHappened()
    {
        var thresholdTitle = QuotaAlertRules.BuildTitle(QuotaAlertRules.ThresholdNotificationType, 83);
        var exceededTitle = QuotaAlertRules.BuildTitle(QuotaAlertRules.ExceededNotificationType, 137);

        Assert.Contains("83%", thresholdTitle);
        Assert.Contains("137%", exceededTitle);
        // Hai trạng thái khác hẳn nhau về mức độ nghiêm trọng nên tiêu đề không được trùng nhau.
        Assert.NotEqual(thresholdTitle, exceededTitle);

        var body = QuotaAlertRules.BuildBody(
            QuotaAlertRules.ThresholdNotificationType, "Cục Trồng trọt", "Gói 1K", 1_250, 1_500, 83);
        Assert.Contains("Cục Trồng trọt", body);
        Assert.Contains("Gói 1K", body);
        // Số token phải đọc được: có phân cách nghìn, không phải "1250 / 1500".
        Assert.Contains("1,250", body);
        Assert.Contains("1,500", body);

        var exceededBody = QuotaAlertRules.BuildBody(
            QuotaAlertRules.ExceededNotificationType, "Cục Trồng trọt", "Gói 1K", 1_250, 1_500, 83);
        Assert.NotEqual(body, exceededBody);
    }
}

/// <summary>
/// "Đã dùng bao nhiêu token tháng này" phải gồm CẢ lượt chat lẫn lần chạy agent, và phải cắt
/// đúng mốc đầu tháng dương lịch. Nếu chỉ đếm chat thì worker cảnh báo 80% sẽ im lặng đúng với
/// những đơn vị đốt token mạnh nhất — thứ mà cảnh báo sinh ra để bắt.
/// </summary>
public sealed class TenantQuotaUsageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DateTime _monthStart = TenantQuotaUsage.MonthStartUtc(DateTime.UtcNow);

    public TenantQuotaUsageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Cục Trồng trọt" });
        db.Tenants.Add(new Tenant { Id = _otherTenantId, Name = "Cục Thủy sản" });
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

    private void AddChatUsage(Guid tenantId, int prompt, int completion, DateTime createdAt)
    {
        using var db = NewContext();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = _userId, Title = "chat"
        };
        db.ChatSessions.Add(session);
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = session.Id, Role = "assistant", Content = "đáp", CreatedAt = createdAt,
            PromptTokens = prompt, CompletionTokens = completion
        });
        db.SaveChanges();
    }

    private void AddAgentUsage(Guid tenantId, int prompt, int completion, DateTime createdAtUtc)
    {
        using var db = NewContext();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = _userId, Title = "agent", IsAgentRun = true
        };
        db.ChatSessions.Add(session);
        db.AgentRuns.Add(new AgentRun
        {
            TenantId = tenantId, UserId = _userId, ChatSessionId = session.Id, Goal = "việc",
            Status = "Completed", CreatedAtUtc = createdAtUtc,
            PromptTokens = prompt, CompletionTokens = completion, ModelName = "gemma4:e4b"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task SumsChatAndAgentRun_PerTenant()
    {
        AddChatUsage(_tenantId, 150, 50, DateTime.UtcNow);
        AddChatUsage(_tenantId, 250, 150, DateTime.UtcNow);
        // Đơn vị thứ hai CHỈ chạy agent, không có một lượt chat nào.
        AddAgentUsage(_otherTenantId, 900, 300, DateTime.UtcNow);

        using var db = NewContext();
        var used = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(db, _monthStart, CancellationToken.None);

        Assert.Equal(600, used[_tenantId]);
        Assert.Equal(1_200, used[_otherTenantId]);
    }

    [Fact]
    public async Task AddsBothSources_WhenATenantHasChatAndAgentRuns()
    {
        AddChatUsage(_tenantId, 400, 200, DateTime.UtcNow);
        AddAgentUsage(_tenantId, 900, 300, DateTime.UtcNow);

        using var db = NewContext();
        var used = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(db, _monthStart, CancellationToken.None);

        Assert.Equal(1_800, used[_tenantId]);
    }

    [Fact]
    public async Task ExcludesEverythingBeforeTheMonthStart()
    {
        var lastMonth = _monthStart.AddDays(-1);
        AddChatUsage(_tenantId, 10_000, 10_000, lastMonth);
        AddAgentUsage(_tenantId, 10_000, 10_000, lastMonth);
        // Một dòng đúng một phút sau mốc đầu tháng: hạn mức reset theo tháng nên mốc cắt phải
        // chính xác tới phút, không phải "trong vòng 30 ngày gần đây".
        AddChatUsage(_otherTenantId, 7, 3, _monthStart.AddMinutes(1));

        using var db = NewContext();
        var used = await TenantQuotaUsage.GetMonthlyUsedByTenantAsync(db, _monthStart, CancellationToken.None);

        Assert.False(used.ContainsKey(_tenantId));
        Assert.Equal(10, used[_otherTenantId]);
    }
}

/// <summary>
/// Worker cảnh báo chủ động: chạy trên SQLite thật để kiểm cả đường truy vấn lẫn việc chống spam
/// — hai thứ dễ sai nhất ở một job lặp. Test gọi thẳng một vòng quét, không dựng host: cảnh báo
/// hạn mức là hậu quả của dữ liệu, không phải của HTTP.
/// </summary>
public sealed class QuotaThresholdWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DateTime _monthStart = TenantQuotaUsage.MonthStartUtc(DateTime.UtcNow);

    public QuotaThresholdWorkerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private Guid AddTenant(string name)
    {
        using var db = NewContext();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = name };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private void AssignPlan(Guid tenantId, int monthlyTokenLimit, bool isActive = true)
    {
        using var db = NewContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Name = $"Gói {monthlyTokenLimit}", MonthlyTokenLimit = monthlyTokenLimit
        };
        db.Plans.Add(plan);
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), TenantId = tenantId, PlanId = plan.Id, IsActive = isActive
        });
        db.SaveChanges();
    }

    private Guid AddUser(Guid tenantId, string role, string name)
    {
        using var db = NewContext();
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Username = $"{name}@{tenantId:N}"[..20],
            Email = $"{name}@{tenantId:N}.test", PasswordHash = "x", Role = role, FullName = name
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private void AddChatUsage(Guid tenantId, Guid userId, int prompt, int completion)
    {
        using var db = NewContext();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Title = "chat"
        };
        db.ChatSessions.Add(session);
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = session.Id, Role = "assistant", Content = "đáp", CreatedAt = DateTime.UtcNow,
            PromptTokens = prompt, CompletionTokens = completion
        });
        db.SaveChanges();
    }

    private void AddAgentUsage(Guid tenantId, Guid userId, int prompt, int completion)
    {
        using var db = NewContext();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Title = "agent", IsAgentRun = true
        };
        db.ChatSessions.Add(session);
        db.AgentRuns.Add(new AgentRun
        {
            TenantId = tenantId, UserId = userId, ChatSessionId = session.Id, Goal = "việc",
            Status = "Completed", CreatedAtUtc = DateTime.UtcNow,
            PromptTokens = prompt, CompletionTokens = completion, ModelName = "gemma4:e4b"
        });
        db.SaveChanges();
    }

    private async Task RunOnceAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var worker = new QuotaThresholdWorker(provider, NullLogger<QuotaThresholdWorker>.Instance);
        await worker.RunOnceAsync(scope.ServiceProvider, CancellationToken.None);
    }

    private List<Notification> Notifications()
    {
        using var db = NewContext();
        return db.Notifications.AsNoTracking().OrderBy(n => n.CreatedAtUtc).ToList();
    }

    [Fact]
    public async Task BelowTheThreshold_WritesNothing()
    {
        var tenantId = AddTenant("Cục Trồng trọt");
        AssignPlan(tenantId, 1000);
        var adminId = AddUser(tenantId, "Admin", "Admin A");
        AddChatUsage(tenantId, adminId, 400, 300); // 70%

        await RunOnceAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task AtEightyPercent_NotifiesEveryAdminOfThatTenant_OncePerMonth()
    {
        var tenantId = AddTenant("Cục Trồng trọt");
        AssignPlan(tenantId, 1000);
        var firstAdmin = AddUser(tenantId, "Admin", "Admin A");
        var secondAdmin = AddUser(tenantId, "Admin", "Admin B");
        AddUser(tenantId, "Member", "Member C");
        AddChatUsage(tenantId, firstAdmin, 500, 300); // đúng 80%

        await RunOnceAsync();

        var written = Notifications();
        Assert.Equal(2, written.Count);
        Assert.All(written, n =>
        {
            Assert.Equal(tenantId, n.TenantId);
            Assert.Equal(QuotaAlertRules.ThresholdNotificationType, n.Type);
            Assert.False(n.IsRead);
            Assert.Contains("80%", n.Title);
        });
        // Chỉ ADMIN của đơn vị: Member không có quyền cấp thêm hạn mức nên thông báo gửi cho họ
        // là thông báo không thể hành động.
        Assert.Equal(
            new[] { firstAdmin, secondAdmin }.OrderBy(id => id),
            written.Select(n => n.UserId).OrderBy(id => id));

        // Vòng quét thứ hai trong CÙNG tháng không được ném thêm thông báo: worker chạy mỗi 15
        // phút, không chặn ở đây thì mỗi tick là một thông báo y hệt.
        await RunOnceAsync();
        Assert.Equal(2, Notifications().Count);
    }

    [Fact]
    public async Task CrossingTheLimit_AddsASeparateExceededAlert()
    {
        var tenantId = AddTenant("Cục Trồng trọt");
        AssignPlan(tenantId, 1000);
        var adminId = AddUser(tenantId, "Admin", "Admin A");
        AddChatUsage(tenantId, adminId, 500, 300);
        await RunOnceAsync();

        AddChatUsage(tenantId, adminId, 100, 100); // 1000 = chạm trần
        await RunOnceAsync();

        var written = Notifications();
        Assert.Equal(2, written.Count);
        // Cảnh báo "sắp chạm" không được thay thế cảnh báo "đã vượt"; đây là hai mốc khác nhau và
        // cái thứ hai mới là cái cần hành động.
        Assert.Contains(written, n => n.Type == QuotaAlertRules.ThresholdNotificationType);
        var exceeded = Assert.Single(written, n => n.Type == QuotaAlertRules.ExceededNotificationType);
        Assert.Contains("100%", exceeded.Title);

        // Và vẫn không lặp lại ở vòng sau.
        await RunOnceAsync();
        Assert.Equal(2, Notifications().Count);
    }

    [Fact]
    public async Task AgentRunSpend_Alone_IsEnoughToTriggerTheAlert()
    {
        var tenantId = AddTenant("Cục Thủy sản");
        AssignPlan(tenantId, 1000);
        var adminId = AddUser(tenantId, "Admin", "Admin A");
        // Không có một lượt chat nào: toàn bộ hạn mức bị đốt bởi một lần chạy agent nhiều bước.
        AddAgentUsage(tenantId, adminId, 900, 300); // 120%

        await RunOnceAsync();

        var notification = Assert.Single(Notifications());
        Assert.Equal(adminId, notification.UserId);
        // Đây chính là trường hợp mà cột hạn mức cũ (chỉ đếm chat) bỏ sót hoàn toàn.
        Assert.Equal(QuotaAlertRules.ExceededNotificationType, notification.Type);
        Assert.Contains("1,200", notification.Body);
    }

    [Fact]
    public async Task UnlimitedPlans_AndTenantsWithoutAPlan_NeverAlert()
    {
        var unlimited = AddTenant("Gói nội bộ");
        AssignPlan(unlimited, 0);
        var unlimitedAdmin = AddUser(unlimited, "Admin", "Admin U");
        AddChatUsage(unlimited, unlimitedAdmin, 900_000, 900_000);

        var noPlan = AddTenant("Chưa gán gói");
        var noPlanAdmin = AddUser(noPlan, "Admin", "Admin N");
        AddChatUsage(noPlan, noPlanAdmin, 900_000, 900_000);

        await RunOnceAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task InactiveSubscription_IsIgnored_SoARemovedPlanStopsAlerting()
    {
        var tenantId = AddTenant("Cục Trồng trọt");
        AssignPlan(tenantId, 1000, isActive: false);
        var adminId = AddUser(tenantId, "Admin", "Admin A");
        AddChatUsage(tenantId, adminId, 900, 900);

        await RunOnceAsync();

        Assert.Empty(Notifications());
    }

    [Fact]
    public async Task TenantWithNoAdmin_IsSkipped_WithoutBreakingOtherTenants()
    {
        var noAdminTenant = AddTenant("Đơn vị không có quản trị");
        AssignPlan(noAdminTenant, 100);
        var memberId = AddUser(noAdminTenant, "Member", "Member M");
        AddChatUsage(noAdminTenant, memberId, 900, 900);

        var healthyTenant = AddTenant("Cục Trồng trọt");
        AssignPlan(healthyTenant, 1000);
        var healthyAdmin = AddUser(healthyTenant, "Admin", "Admin A");
        AddChatUsage(healthyTenant, healthyAdmin, 500, 300);

        await RunOnceAsync();

        // Đơn vị không có Admin bị bỏ qua (không có ai để nhận), nhưng không được kéo theo việc
        // bỏ luôn cảnh báo của đơn vị khác trong cùng vòng quét.
        var notification = Assert.Single(Notifications());
        Assert.Equal(healthyTenant, notification.TenantId);
        Assert.Equal(healthyAdmin, notification.UserId);
    }

    [Fact]
    public async Task EachBreachingTenantGetsItsOwnAlert_AndTheyDoNotLeakAcrossTenants()
    {
        var first = AddTenant("Cục Trồng trọt");
        AssignPlan(first, 1000);
        var firstAdmin = AddUser(first, "Admin", "Admin A");
        AddChatUsage(first, firstAdmin, 500, 300); // 80%

        var second = AddTenant("Cục Thủy sản");
        AssignPlan(second, 100);
        var secondAdmin = AddUser(second, "Admin", "Admin B");
        AddChatUsage(second, secondAdmin, 90, 20); // 110%

        await RunOnceAsync();

        var written = Notifications();
        Assert.Equal(2, written.Count);

        var firstAlert = Assert.Single(written, n => n.TenantId == first);
        Assert.Equal(firstAdmin, firstAlert.UserId);
        Assert.Equal(QuotaAlertRules.ThresholdNotificationType, firstAlert.Type);
        // Thông báo của đơn vị này không được mang số liệu của đơn vị kia.
        Assert.DoesNotContain("Cục Thủy sản", firstAlert.Body);

        var secondAlert = Assert.Single(written, n => n.TenantId == second);
        Assert.Equal(secondAdmin, secondAlert.UserId);
        Assert.Equal(QuotaAlertRules.ExceededNotificationType, secondAlert.Type);
        Assert.Contains("Cục Thủy sản", secondAlert.Body);
    }
}
