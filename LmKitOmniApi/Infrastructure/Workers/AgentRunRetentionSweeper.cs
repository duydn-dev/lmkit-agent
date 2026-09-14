using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.Workers;

/// <summary>
/// Cấu hình dọn dữ liệu Automation Agent, bound từ section "Retention".
/// Lịch chạy chế độ agent sinh AgentRun + phiên chat ẩn + notification MỖI LẦN
/// chạy (lịch 30 phút ≈ 48 run/ngày) — không dọn thì bảng phình vô hạn.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>
    /// Số ngày giữ AgentRun ĐÃ KẾT THÚC (Completed/Failed/CompletedAfterApproval/
    /// Rejected/Expired — nhận diện qua CompletedAtUtc đã set; run đang chạy hoặc
    /// đang chờ phê duyệt KHÔNG BAO GIỜ bị dọn). 0 hoặc âm = tắt dọn.
    /// </summary>
    public int AgentRunDays { get; set; } = 30;

    /// <summary>Số ngày giữ notification ĐÃ ĐỌC. 0 hoặc âm = tắt dọn.</summary>
    public int NotificationDays { get; set; } = 90;
}

/// <summary>
/// Quét dọn sản phẩm phụ của Automation Agent: run đã kết thúc quá hạn
/// (kèm step + phiên chat ẩn + message của phiên đó) và notification đã đọc
/// quá hạn. Được <see cref="DataRetentionWorker"/> gọi mỗi chu kỳ; tách static
/// để test gọi thẳng với DbContext SQLite.
/// </summary>
public static class AgentRunRetentionSweeper
{
    public sealed record SweepResult(int Runs, int Steps, int Sessions, int Messages, int Notifications);

    public static async Task<SweepResult> SweepAsync(
        HermesDbContext db, RetentionOptions options, DateTime nowUtc, CancellationToken ct)
    {
        int runs = 0, steps = 0, sessions = 0, messages = 0, notifications = 0;

        if (options.AgentRunDays > 0)
        {
            var cutoff = nowUtc.AddDays(-options.AgentRunDays);

            // Run kết thúc = CompletedAtUtc đã set (Running/AwaitingApproval giữ null
            // — xem StreamAgentRunCommandHandler.FinalizeAsync — nên không lọt lưới).
            var expiredRuns = db.AgentRuns
                .Where(run => run.CompletedAtUtc != null && run.CompletedAtUtc < cutoff);

            // Lấy id phiên ẩn TRƯỚC khi xóa run (sau đó không còn gì trỏ tới chúng).
            var sessionIds = await expiredRuns
                .Select(run => run.ChatSessionId)
                .Distinct()
                .ToListAsync(ct);

            steps = await db.AgentRunSteps
                .Where(step => expiredRuns.Any(run => run.Id == step.AgentRunId))
                .ExecuteDeleteAsync(ct);
            runs = await expiredRuns.ExecuteDeleteAsync(ct);

            if (sessionIds.Count > 0)
            {
                // Chỉ phiên ẨN của agent-run; phiên chat thường trùng id (không thể) hay
                // được tham chiếu bởi run khác chưa hết hạn thì giữ nguyên.
                var stillReferenced = await db.AgentRuns
                    .Where(run => sessionIds.Contains(run.ChatSessionId))
                    .Select(run => run.ChatSessionId)
                    .Distinct()
                    .ToListAsync(ct);
                var deletableSessionIds = sessionIds.Except(stillReferenced).ToList();

                if (deletableSessionIds.Count > 0)
                {
                    // Chốt danh sách theo cờ IsAgentRun trước, rồi mới xóa message + phiên
                    // theo đúng danh sách đó — message của phiên chat thường không bao giờ
                    // bị chạm dù dữ liệu có bất thường.
                    var hiddenSessionIds = await db.ChatSessions
                        .Where(session => deletableSessionIds.Contains(session.Id) && session.IsAgentRun)
                        .Select(session => session.Id)
                        .ToListAsync(ct);
                    if (hiddenSessionIds.Count > 0)
                    {
                        messages = await db.ChatMessages
                            .Where(message => hiddenSessionIds.Contains(message.ChatSessionId))
                            .ExecuteDeleteAsync(ct);
                        sessions = await db.ChatSessions
                            .Where(session => hiddenSessionIds.Contains(session.Id))
                            .ExecuteDeleteAsync(ct);
                    }
                }
            }
        }

        if (options.NotificationDays > 0)
        {
            var cutoff = nowUtc.AddDays(-options.NotificationDays);
            notifications = await db.Notifications
                .Where(notification => notification.IsRead && notification.CreatedAtUtc < cutoff)
                .ExecuteDeleteAsync(ct);
        }

        return new SweepResult(runs, steps, sessions, messages, notifications);
    }
}
