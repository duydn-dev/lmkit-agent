using System;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Bug thật đã đo được: bật lại lịch "once" bằng toggle gọi
/// <see cref="ScheduleCalculator.ComputeNextRun"/> mà hàm này không có nhánh
/// "once" → InvalidOperationException → API 500, nút "Bật/tắt" trên app vô hiệu
/// với loại lịch một lần. Khóa hành vi: "once" giữ nguyên mốc hẹn, không ném.
/// </summary>
public class ScheduleCalculatorToggleOnceTests
{
    private static ScheduledTask OnceTask(DateTime runAt) => new()
    {
        ScheduleKind = ScheduleCalculator.OnceKind,
        NextRunUtc = runAt,
    };

    [Fact]
    public void ComputeNextRun_once_keeps_user_anchor_instead_of_throwing()
    {
        var runAt = new DateTime(2026, 9, 19, 3, 0, 0, DateTimeKind.Utc);
        var task = OnceTask(runAt);

        var next = ScheduleCalculator.ComputeNextRun(task, new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(runAt, next);
    }

    [Fact]
    public void ComputeNextRun_once_without_anchor_still_throws()
    {
        // NextRunUtc là DateTime không-nullable trên entity; dữ liệu hỏng ở đây là
        // giá trị mặc định (default) — vẫn phải ném thay vì trả mốc vô nghĩa.
        var task = new ScheduledTask { ScheduleKind = ScheduleCalculator.OnceKind };
        Assert.Equal(default, task.NextRunUtc);

        Assert.Throws<InvalidOperationException>(
            () => ScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow));
    }

    [Fact]
    public void ComputeNextRun_unknown_kind_message_includes_once()
    {
        var task = new ScheduledTask { ScheduleKind = "hourly" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow));

        Assert.Contains("once", ex.Message);
    }
}
