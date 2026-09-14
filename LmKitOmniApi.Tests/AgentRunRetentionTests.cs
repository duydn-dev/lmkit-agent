using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Vòng đời dữ liệu Automation Agent: run kết thúc quá hạn bị dọn KÈM step,
/// phiên chat ẩn và message của phiên; run đang chạy/chờ phê duyệt và dữ liệu
/// còn trong hạn giữ nguyên; cấu hình 0 ngày = tắt hẳn.
/// </summary>
public sealed class AgentRunRetentionTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AgentRunRetentionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        // FK gốc: mọi run/phiên/notification đều trỏ về tenant + user này.
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Tenant kiểm thử" });
        db.Users.Add(new User
        {
            Id = UserId,
            TenantId = TenantId,
            Username = "retention",
            Email = "retention@example.test",
            PasswordHash = "hash",
            FullName = "Retention Tester"
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private HermesDbContext CreateContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (AgentRun Run, ChatSession Session) BuildRun(
        string status, DateTime? completedAtUtc, int steps = 2, bool withMessages = true)
    {
        var session = new ChatSession
        {
            TenantId = TenantId,
            UserId = UserId,
            Title = "run ẩn",
            IsAgentRun = true
        };
        if (withMessages)
        {
            session.Messages.Add(new ChatMessage { Role = "user", Content = "goal" });
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "kết quả" });
        }
        var run = new AgentRun
        {
            TenantId = TenantId,
            UserId = UserId,
            ChatSessionId = session.Id,
            Goal = "Đếm đơn hàng hôm qua",
            Status = status,
            CompletedAtUtc = completedAtUtc
        };
        for (var i = 1; i <= steps; i++)
        {
            run.Steps.Add(new AgentRunStep
            {
                Ordinal = i,
                Action = "run_database_query",
                Input = "SELECT 1",
                Observation = "1"
            });
        }
        return (run, session);
    }

    [Fact]
    public async Task Sweep_RemovesExpiredTerminalRuns_WithStepsHiddenSessionAndMessages()
    {
        var now = DateTime.UtcNow;
        await using (var db = CreateContext())
        {
            var (oldRun, oldSession) = BuildRun(AgentRunStatuses.Completed, now.AddDays(-40));
            var (freshRun, freshSession) = BuildRun(AgentRunStatuses.Completed, now.AddDays(-5));
            var (parked, parkedSession) = BuildRun(AgentRunStatuses.AwaitingApproval, completedAtUtc: null);
            db.AddRange(oldSession, freshSession, parkedSession, oldRun, freshRun, parked);
            db.Notifications.Add(new Notification
            {
                TenantId = TenantId, UserId = UserId, Type = "scheduled", Title = "cũ đã đọc",
                Body = "x", IsRead = true, CreatedAtUtc = now.AddDays(-120)
            });
            db.Notifications.Add(new Notification
            {
                TenantId = TenantId, UserId = UserId, Type = "scheduled", Title = "cũ CHƯA đọc",
                Body = "x", IsRead = false, CreatedAtUtc = now.AddDays(-120)
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var result = await AgentRunRetentionSweeper.SweepAsync(
                db, new RetentionOptions { AgentRunDays = 30, NotificationDays = 90 }, now, CancellationToken.None);

            Assert.Equal(1, result.Runs);
            Assert.Equal(2, result.Steps);
            Assert.Equal(1, result.Sessions);
            Assert.Equal(2, result.Messages);
            Assert.Equal(1, result.Notifications); // chỉ notification ĐÃ ĐỌC quá hạn
        }

        await using (var verify = CreateContext())
        {
            Assert.Equal(2, await verify.AgentRuns.CountAsync());                       // fresh + parked
            Assert.Equal(4, await verify.AgentRunSteps.CountAsync());                   // 2 run × 2 step
            Assert.Equal(2, await verify.ChatSessions.CountAsync(s => s.IsAgentRun));
            Assert.Single(await verify.Notifications.ToListAsync());                    // bản chưa đọc còn nguyên
            Assert.False((await verify.Notifications.SingleAsync()).IsRead);
        }
    }

    [Fact]
    public async Task Sweep_NeverTouchesRunningOrAwaitingApprovalRuns_EvenWhenOld()
    {
        var now = DateTime.UtcNow;
        await using (var db = CreateContext())
        {
            // CompletedAtUtc null là bất biến của run chưa kết thúc — kể cả tạo từ lâu.
            var (running, runningSession) = BuildRun(AgentRunStatuses.Running, completedAtUtc: null);
            running.CreatedAtUtc = now.AddDays(-365);
            var (parked, parkedSession) = BuildRun(AgentRunStatuses.AwaitingApproval, completedAtUtc: null);
            parked.CreatedAtUtc = now.AddDays(-365);
            db.AddRange(runningSession, parkedSession, running, parked);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var result = await AgentRunRetentionSweeper.SweepAsync(
                db, new RetentionOptions { AgentRunDays = 30, NotificationDays = 90 }, now, CancellationToken.None);
            Assert.Equal(0, result.Runs);
            Assert.Equal(0, result.Sessions);
        }

        await using (var verify = CreateContext())
        {
            Assert.Equal(2, await verify.AgentRuns.CountAsync());
        }
    }

    [Fact]
    public async Task Sweep_WithZeroDays_IsDisabledEntirely()
    {
        var now = DateTime.UtcNow;
        await using (var db = CreateContext())
        {
            var (oldRun, oldSession) = BuildRun(AgentRunStatuses.Failed, now.AddDays(-400));
            db.AddRange(oldSession, oldRun);
            db.Notifications.Add(new Notification
            {
                TenantId = TenantId, UserId = UserId, Type = "scheduled", Title = "rất cũ",
                Body = "x", IsRead = true, CreatedAtUtc = now.AddDays(-400)
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var result = await AgentRunRetentionSweeper.SweepAsync(
                db, new RetentionOptions { AgentRunDays = 0, NotificationDays = 0 }, now, CancellationToken.None);
            Assert.Equal(new AgentRunRetentionSweeper.SweepResult(0, 0, 0, 0, 0), result);
        }

        await using (var verify = CreateContext())
        {
            Assert.Equal(1, await verify.AgentRuns.CountAsync());
            Assert.Equal(1, await verify.Notifications.CountAsync());
        }
    }
}
