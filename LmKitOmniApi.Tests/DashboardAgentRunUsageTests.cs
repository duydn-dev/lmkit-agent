using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Dashboard;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Bộ ước lượng token của agent-run — công thức mà CẢ lượt chạy đầu
/// (<c>StreamAgentRunCommandHandler</c>) lẫn mỗi lần chạy lại sau phê duyệt
/// (<c>AgentRunResumeService</c>) phải cùng cho ra một kết quả. Test ở đây là hàng rào cho
/// "hai đường phải cộng ra một con số": lệch công thức là dashboard báo sai chi phí.
/// </summary>
public sealed class AgentRunTokenUsageTests
{
    /// <summary>
    /// Bộ đếm giả: mỗi ký tự = 1 token. Số học của công thức nhìn được bằng mắt, không phụ thuộc
    /// vào bảng tỉ lệ của TokenManagementService.
    /// </summary>
    private sealed class CharCountTokens : ITokenManagementService
    {
        public int EstimateTokenCount(string text) => text.Length;

        public Task<TrimmedHistoryResult> TrimHistoryAsync(
            List<HistoryMessage> messages, int maxTokenBudget, CancellationToken ct = default)
            => Task.FromResult(new TrimmedHistoryResult());
    }

    private static readonly ITokenManagementService Tokens = new CharCountTokens();

    [Fact]
    public void Estimate_CountsObservationsAsPrompt_AndToolInputsPlusAnswerAsCompletion()
    {
        var steps = new[]
        {
            new AgentRunStepData("SQL", "SELECT 1", "kq-1"),
            new AgentRunStepData("RAG", "truy van", "kq-2")
        };

        var usage = AgentRunTokenUsage.Estimate(Tokens, "muc-tieu", steps, "tra-loi");

        // prompt = goal "muc-tieu"(8) + "kq-1"(4) + "kq-2"(4); observation của mỗi bước được
        // nạp lại vào lượt suy luận kế tiếp nên phải tính — bỏ qua nó là đếm thiếu đúng phần
        // đắt nhất của một lần chạy nhiều bước.
        Assert.Equal(16, usage.PromptTokens);
        // completion = payload gọi tool model tự sinh ("SELECT 1" 8 + "truy van" 8) + câu trả
        // lời cuối "tra-loi"(7).
        Assert.Equal(23, usage.CompletionTokens);
    }

    [Fact]
    public void Estimate_IgnoresRefusalStep_SoAnAdmissionRefusalCostsNothing()
    {
        var steps = new[]
        {
            new AgentRunStepData(AgentRunStepData.AdmissionRefusedAction, string.Empty, "hang doi day")
        };

        var usage = AgentRunTokenUsage.Estimate(Tokens, "muc-tieu", steps, string.Empty);

        // Model chưa hề chạy: tính bước từ chối là bịa ra chi phí chưa từng tồn tại.
        Assert.Equal(8, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
    }

    [Fact]
    public void Estimate_EmptyFinalText_DoesNotAddCompletionTokens()
    {
        var usage = AgentRunTokenUsage.Estimate(Tokens, "g", [], string.Empty);

        Assert.Equal(1, usage.PromptTokens);
        Assert.Equal(0, usage.CompletionTokens);
    }

    [Fact]
    public void HasModelWork_IsFalseForARefusedRun_AndTrueOnceAnythingRan()
    {
        var refusalOnly = new[] { new AgentRunStepData(AgentRunStepData.AdmissionRefusedAction, "", "busy") };

        Assert.False(AgentRunTokenUsage.HasModelWork(refusalOnly, completed: false, awaitingApproval: false));
        Assert.True(AgentRunTokenUsage.HasModelWork(refusalOnly, completed: true, awaitingApproval: false));
        Assert.True(AgentRunTokenUsage.HasModelWork(refusalOnly, completed: false, awaitingApproval: true));
        Assert.True(AgentRunTokenUsage.HasModelWork(
            [new AgentRunStepData("SQL", "SELECT 1", "kq")], completed: false, awaitingApproval: false));
    }

    [Fact]
    public void PeriodNormalize_OnlyAcceptsTheThreeDocumentedWindows()
    {
        Assert.Equal(7, DashboardPeriod.Normalize(7));
        Assert.Equal(30, DashboardPeriod.Normalize(30));
        Assert.Equal(90, DashboardPeriod.Normalize(90));
        // Ngoài bộ ba hợp lệ thì rơi về mặc định, không ném và không nhận bừa.
        Assert.Equal(30, DashboardPeriod.Normalize(13));
        Assert.Equal(30, DashboardPeriod.Normalize(-5));
        Assert.Equal(30, DashboardPeriod.Normalize(0));
        Assert.Equal(30, DashboardPeriod.Normalize(null));
    }
}

/// <summary>
/// Chi phí agent-run phải xuất hiện trên dashboard ở ĐÚNG ô của nó: cột token riêng, không trộn
/// vào lượt chat, và không tính lần chạy chưa từng gọi model. Test chạy trên handler thật với
/// SQLite nên kiểm được cả truy vấn lẫn hình dạng JSON trả về cho web/mobile.
/// </summary>
public sealed class DashboardAgentRunUsageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public DashboardAgentRunUsageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private void Seed(HermesDbContext db)
    {
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Cục Trồng trọt" });
        db.Tenants.Add(new Tenant { Id = _otherTenantId, Name = "Cục Thủy sản" });

        db.Users.Add(new User
        {
            Id = _adminId, TenantId = _tenantId, Username = "admin", Email = "admin@t.test",
            PasswordHash = "x", Role = "Admin", FullName = "Admin User"
        });
        db.Users.Add(new User
        {
            Id = _memberId, TenantId = _tenantId, Username = "member", Email = "member@t.test",
            PasswordHash = "x", Role = "Member", FullName = "Member User"
        });

        // Chat: một phiên thường với 2 lượt trả lời (150/50 và 250/150) trong kỳ.
        var chatSession = new ChatSession { Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _memberId, Title = "chat" };
        db.ChatSessions.Add(chatSession);
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = chatSession.Id, Role = "user", Content = "hỏi", CreatedAt = DateTime.UtcNow
        });
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = chatSession.Id, Role = "assistant", Content = "đáp 1", CreatedAt = DateTime.UtcNow,
            PromptTokens = 150, CompletionTokens = 50, ModelName = "gemma4:e4b", LatencyMs = 1_000
        });
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = chatSession.Id, Role = "assistant", Content = "đáp 2", CreatedAt = DateTime.UtcNow,
            PromptTokens = 250, CompletionTokens = 150, ModelName = "gemma4:e4b", LatencyMs = 3_000
        });

        // Mỗi lần chạy phải có phiên ẩn của nó: FK agent_runs -> chat_sessions là bắt buộc
        // (SQLite bật FK nên thiếu phiên là SaveChanges đổ ngay).
        Guid AgentSession(Guid tenantId, Guid userId, string title)
        {
            var session = new ChatSession
            {
                Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Title = title, IsAgentRun = true
            };
            db.ChatSessions.Add(session);
            return session.Id;
        }

        // Agent-run có gọi model: 400/100, đo được độ trễ, thuộc đơn vị thứ hai (để kiểm phần
        // chi tiêu theo đơn vị và việc đơn vị chỉ chạy agent vẫn phải xuất hiện).
        db.AgentRuns.Add(new AgentRun
        {
            TenantId = _otherTenantId, UserId = _adminId,
            ChatSessionId = AgentSession(_otherTenantId, _adminId, "Tổng hợp báo cáo"),
            Goal = "Tổng hợp báo cáo", Status = "Completed", CreatedAtUtc = DateTime.UtcNow,
            PromptTokens = 900, CompletionTokens = 300, ModelName = "qwen3:8b", LatencyMs = 9_000
        });

        // Lần chạy bị hàng đợi từ chối: hai cột token bằng 0 -> không được đếm là một lần chạy.
        db.AgentRuns.Add(new AgentRun
        {
            TenantId = _tenantId, UserId = _memberId,
            ChatSessionId = AgentSession(_tenantId, _memberId, "việc bị từ chối"),
            Goal = "việc bị từ chối", Status = "Failed", CreatedAtUtc = DateTime.UtcNow
        });

        // Lần chạy 40 ngày trước: ngoài cửa sổ 30 ngày, chỉ thấy ở kỳ 90 ngày.
        db.AgentRuns.Add(new AgentRun
        {
            TenantId = _tenantId, UserId = _memberId,
            ChatSessionId = AgentSession(_tenantId, _memberId, "việc cũ"),
            Goal = "việc cũ", Status = "Completed", CreatedAtUtc = DateTime.UtcNow.AddDays(-40),
            PromptTokens = 10, CompletionTokens = 5, ModelName = "gemma4:e4b", LatencyMs = 500
        });

        db.SaveChanges();
    }

    private GetDashboardStatsQueryHandler StatsHandler() => new(NewContext());

    private static GetDashboardStatsQuery Stats(Guid userId, bool isAdmin, int days) => new()
    {
        TenantId = Guid.NewGuid(), UserId = userId, IsAdmin = isAdmin, PeriodDays = days
    };

    [Fact]
    public async Task AdminSeesAgentRunCost_InItsOwnBucket_WithChatExcluded()
    {
        var stats = await StatsHandler().Handle(Stats(_adminId, isAdmin: true, 30), CancellationToken.None);
        var tokens = stats.Cockpit!.Tokens;

        // Chat giữ nguyên nghĩa cũ: 150+250 prompt, 50+150 completion.
        Assert.Equal(400, tokens.PromptTokens);
        Assert.Equal(200, tokens.CompletionTokens);
        Assert.Equal(2, tokens.Messages);

        // Agent-run nằm ở cột riêng: 900/300 và đúng 1 lần chạy có gọi model (lần bị từ chối
        // và lần 40 ngày trước đều không tính).
        Assert.Equal(900, tokens.AgentRunPromptTokens);
        Assert.Equal(300, tokens.AgentRunCompletionTokens);
        Assert.Equal(1, tokens.AgentRuns);
    }

    [Fact]
    public async Task Spend_KeepsBothSources_ApartAndInConcentration()
    {
        var stats = await StatsHandler().Handle(Stats(_adminId, isAdmin: true, 30), CancellationToken.None);
        var spend = stats.Cockpit!.Spend;

        Assert.Equal(600, spend.TotalTokens);
        Assert.Equal(1_200, spend.TotalAgentRunTokens);

        var withRuns = spend.ByTenant.Single(t => t.TenantId == _otherTenantId);
        Assert.Equal(0, withRuns.PromptTokens);
        Assert.Equal(900, withRuns.AgentRunPromptTokens);
        Assert.Equal(300, withRuns.AgentRunCompletionTokens);
        Assert.Equal(1, withRuns.AgentRuns);
        // Đơn vị chỉ chạy agent mà không chat vẫn phải có dòng, nếu không chi phí lớn nhất vô hình.
        Assert.Equal("Cục Thủy sản", withRuns.TenantName);

        var chatOnly = spend.ByTenant.Single(t => t.TenantId == _tenantId);
        Assert.Equal(400, chatOnly.PromptTokens);
        Assert.Equal(200, chatOnly.CompletionTokens);
        Assert.Equal(0, chatOnly.AgentRunPromptTokens);
        Assert.Equal(0, chatOnly.AgentRunCompletionTokens);
        Assert.Equal(0, chatOnly.AgentRuns);

        // Tỉ lệ tập trung tính trên TỔNG (600 chat + 1200 agent = 1800), không phải chỉ chat —
        // và vì thế đơn vị đốt token qua AGENT mới là đơn vị đứng đầu, đúng thứ mà bảng chat
        // thuần không nhìn ra.
        Assert.Equal(100, spend.Top3SharePct);
        Assert.Equal(67, spend.TopTenantSharePct);
        Assert.Equal("Cục Thủy sản", spend.TopTenantName);
    }

    [Fact]
    public async Task Performance_SeparatesAgentRunLatency_FromChatLatency()
    {
        var stats = await StatsHandler().Handle(Stats(_adminId, isAdmin: true, 30), CancellationToken.None);
        var performance = stats.Cockpit!.Performance;

        Assert.Equal(2, performance.Samples);
        Assert.Equal(2_000, performance.AvgLatencyMs);
        // p95 theo định nghĩa đang dùng: chỉ số floor((n-1)*0.95) trên mảng đã sắp xếp. Với
        // n=2 chỉ số đó bằng 0, nên p95 là mẫu NHỎ hơn — test ghim đúng định nghĩa này thay vì
        // kỳ vọng trực giác "p95 phải gần mẫu lớn nhất".
        Assert.Equal(1_000, performance.P95LatencyMs);

        Assert.Equal(1, performance.AgentRunSamples);
        Assert.Equal(9_000, performance.AgentRunAvgLatencyMs);
        Assert.Equal(9_000, performance.AgentRunP95LatencyMs);
    }

    [Fact]
    public async Task Window_ExcludesRunsOlderThanThePeriod_ButTheLongWindowSeesThem()
    {
        var nineties = await StatsHandler().Handle(Stats(_adminId, isAdmin: true, 90), CancellationToken.None);
        var seven = await StatsHandler().Handle(Stats(_adminId, isAdmin: true, 7), CancellationToken.None);

        // 90 ngày: thêm lần chạy 40 ngày trước (10/5) -> 910/305 và 2 lần chạy.
        Assert.Equal(910, nineties.Cockpit!.Tokens.AgentRunPromptTokens);
        Assert.Equal(305, nineties.Cockpit.Tokens.AgentRunCompletionTokens);
        Assert.Equal(2, nineties.Cockpit.Tokens.AgentRuns);

        // 7 ngày: chỉ còn lần chạy hôm nay; lần 40 ngày trước nằm ngoài cửa sổ.
        Assert.Equal(900, seven.Cockpit!.Tokens.AgentRunPromptTokens);
        Assert.Equal(1, seven.Cockpit.Tokens.AgentRuns);

        Assert.Equal(90, nineties.PeriodDays);
        Assert.Equal(7, seven.PeriodDays);
    }

    [Fact]
    public async Task Member_GetsNoCockpit_SoNoAgentRunNumbersLeak()
    {
        var stats = await StatsHandler().Handle(Stats(_memberId, isAdmin: false, 30), CancellationToken.None);

        Assert.Null(stats.Cockpit);
        Assert.Equal(2, stats.MyUsage.Answers);
        Assert.Equal(400, stats.MyUsage.PromptTokens);
    }

    [Fact]
    public async Task Csv_AddsAgentRunColumns_WithoutBreakingTheChatColumns()
    {
        var result = await new GetDashboardCsvQueryHandler(NewContext())
            .Handle(new GetDashboardCsvQuery { PeriodDays = 30 }, CancellationToken.None);

        var text = Encoding.UTF8.GetString(result.Content);
        Assert.StartsWith("\uFEFF", text);
        Assert.StartsWith(
            "Mã đơn vị,Tên đơn vị,Model,Số câu trả lời,Prompt tokens,Completion tokens,Tổng token,"
            + "Số lần chạy agent,Prompt tokens agent,Completion tokens agent",
            text[1..]);

        var lines = text[1..].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + (Thủy sản × qwen3:8b) + (Trồng trọt × gemma4:e4b)

        // Đơn vị chỉ chạy agent vẫn có dòng riêng, cột chat bằng 0. Dòng CSV bắt đầu bằng MÃ
        // đơn vị (GUID) rồi mới tới tên, nên phải tìm theo tên chứ không phải theo tiền tố.
        var agentOnly = lines.Single(l => l.Contains("Cục Thủy sản"));
        Assert.Equal("Cục Thủy sản", agentOnly.Split(',')[1]);
        Assert.Contains(",qwen3:8b,0,0,0,0,1,900,300", agentOnly);

        // Dòng chat giữ nguyên 2 lượt trả lời / 400+200 token, và cột agent-run bằng 0.
        var chatOnly = lines.Single(l => l.Contains("Cục Trồng trọt"));
        Assert.Contains(",gemma4:e4b,2,400,200,600,0,0,0", chatOnly);
    }

    [Fact]
    public async Task Csv_MergesChatAndAgentRunOfTheSameTenantAndModel_OnOneRow()
    {
        using (var db = NewContext())
        {
            var session = new ChatSession
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _memberId,
                Title = "cùng model", IsAgentRun = true
            };
            db.ChatSessions.Add(session);
            db.AgentRuns.Add(new AgentRun
            {
                TenantId = _tenantId, UserId = _memberId, ChatSessionId = session.Id,
                Goal = "cùng model", Status = "Completed", CreatedAtUtc = DateTime.UtcNow,
                PromptTokens = 7, CompletionTokens = 3, ModelName = "gemma4:e4b", LatencyMs = 100
            });
            db.SaveChanges();
        }

        var result = await new GetDashboardCsvQueryHandler(NewContext())
            .Handle(new GetDashboardCsvQuery { PeriodDays = 30 }, CancellationToken.None);
        var lines = Encoding.UTF8.GetString(result.Content)[1..]
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Cùng (đơn vị × model) phải là MỘT dòng, không tách thành hai.
        var merged = lines.Single(l => l.Contains("Cục Trồng trọt") && l.Contains("gemma4:e4b"));
        Assert.Contains(",gemma4:e4b,2,400,200,600,1,7,3", merged);
    }
}
