using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules;

/// <summary>
/// Shared validation and mapping for the create/update/toggle scheduled-task handlers so all
/// mutations enforce the exact same rules and Vietnamese 400 messages.
/// </summary>
public static class ScheduledTaskRules
{
    public const int MaxNameLength = 100;
    public const int MaxPromptLength = 2000;
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 10080; // 7 days
    public const int MaxEnabledTasksPerUser = 10;

    public const string EnabledCapMessage =
        "Mỗi người dùng chỉ được bật tối đa 10 lịch tự động. Vui lòng tắt hoặc xóa bớt lịch hiện có.";

    /// <summary>Returns the Vietnamese 400 message for an invalid request, or <c>null</c> when valid.</summary>
    public static string? Validate(SaveScheduledTaskCommandBase request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Tên lịch không được để trống.";
        if (name.Length > MaxNameLength)
            return "Tên lịch không được vượt quá 100 ký tự.";

        var prompt = request.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt))
            return "Nội dung nhắc lệnh không được để trống.";
        if (prompt.Length > MaxPromptLength)
            return "Nội dung nhắc lệnh không được vượt quá 2000 ký tự.";

        switch (NormalizeKind(request.ScheduleKind))
        {
            case ScheduleCalculator.IntervalKind:
                if (request.IntervalMinutes is not (>= MinIntervalMinutes and <= MaxIntervalMinutes))
                    return "Chu kỳ lặp phải từ 15 đến 10080 phút.";
                break;
            case ScheduleCalculator.DailyKind:
                if (request.TimeOfDayMinutes is not (>= 0 and <= 1439))
                    return "Thời điểm chạy trong ngày phải từ 0 đến 1439 phút (theo giờ UTC).";
                break;
            case ScheduleCalculator.WeeklyKind:
                if (request.TimeOfDayMinutes is not (>= 0 and <= 1439))
                    return "Thời điểm chạy trong ngày phải từ 0 đến 1439 phút (theo giờ UTC).";
                if (request.DayOfWeek is not (>= 0 and <= 6))
                    return "Thứ trong tuần phải từ 0 (Chủ nhật) đến 6 (Thứ bảy).";
                break;
            default:
                return "Loại lịch không hợp lệ. Chỉ hỗ trợ: interval, daily, weekly.";
        }

        return null;
    }

    /// <summary>
    /// Copies a validated request onto the entity, nulls out the fields the chosen kind does not
    /// use, and recomputes <see cref="ScheduledTask.NextRunUtc"/> from <paramref name="nowUtc"/>.
    /// </summary>
    public static void Apply(ScheduledTask task, SaveScheduledTaskCommandBase request, DateTime nowUtc)
    {
        var kind = NormalizeKind(request.ScheduleKind);
        task.Name = request.Name.Trim();
        task.Prompt = request.Prompt.Trim();
        task.ScheduleKind = kind;
        task.IntervalMinutes = kind == ScheduleCalculator.IntervalKind ? request.IntervalMinutes : null;
        task.TimeOfDayMinutes = kind is ScheduleCalculator.DailyKind or ScheduleCalculator.WeeklyKind
            ? request.TimeOfDayMinutes
            : null;
        task.DayOfWeek = kind == ScheduleCalculator.WeeklyKind ? request.DayOfWeek : null;
        task.NextRunUtc = ScheduleCalculator.ComputeNextRun(task, nowUtc);
    }

    public static Task<int> CountEnabledAsync(HermesDbContext db, Guid tenantId, Guid userId, CancellationToken ct) =>
        db.ScheduledTasks.CountAsync(
            task => task.TenantId == tenantId && task.UserId == userId && task.Enabled, ct);

    public static ScheduledTaskDto ToDto(ScheduledTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        Prompt = task.Prompt,
        ScheduleKind = task.ScheduleKind,
        IntervalMinutes = task.IntervalMinutes,
        TimeOfDayMinutes = task.TimeOfDayMinutes,
        DayOfWeek = task.DayOfWeek,
        Enabled = task.Enabled,
        NextRunUtc = task.NextRunUtc,
        LastRunUtc = task.LastRunUtc,
        LastStatus = task.LastStatus,
        LastError = task.LastError
    };

    private static string NormalizeKind(string? scheduleKind) =>
        scheduleKind?.Trim().ToLowerInvariant() ?? string.Empty;
}
