using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Pure next-run computation for <see cref="ScheduledTask"/> presets. All math is UTC and
/// side-effect free so the logic is unit-testable without a database or clock abstraction.
/// </summary>
public static class ScheduleCalculator
{
    public const string IntervalKind = "interval";
    public const string DailyKind = "daily";
    public const string WeeklyKind = "weekly";

    /// <summary>Inclusive upper bound for a time-of-day expressed as minutes after midnight UTC (23:59).</summary>
    public const int MaxTimeOfDayMinutes = 1439;

    /// <summary>
    /// Computes the next run strictly after <paramref name="nowUtc"/>:
    /// <list type="bullet">
    ///   <item><c>interval</c> → <paramref name="nowUtc"/> + <see cref="ScheduledTask.IntervalMinutes"/>.</item>
    ///   <item><c>daily</c> → the next occurrence of <see cref="ScheduledTask.TimeOfDayMinutes"/> (UTC)
    ///   strictly after <paramref name="nowUtc"/> (an exactly-now match rolls to the next day).</item>
    ///   <item><c>weekly</c> → the next occurrence of (<see cref="ScheduledTask.DayOfWeek"/>,
    ///   <see cref="ScheduledTask.TimeOfDayMinutes"/>) (UTC) strictly after <paramref name="nowUtc"/>
    ///   (an exactly-now match rolls to the next week).</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> for an unknown kind or a missing/out-of-range
    /// field, so callers surface corrupt definitions instead of silently rescheduling.
    /// </summary>
    public static DateTime ComputeNextRun(ScheduledTask task, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.ScheduleKind?.Trim().ToLowerInvariant() switch
        {
            IntervalKind => ComputeInterval(task, nowUtc),
            DailyKind => ComputeDaily(task, nowUtc),
            WeeklyKind => ComputeWeekly(task, nowUtc),
            var kind => throw new InvalidOperationException(
                $"Unknown schedule kind '{kind}'. Supported kinds: interval, daily, weekly.")
        };
    }

    private static DateTime ComputeInterval(ScheduledTask task, DateTime nowUtc)
    {
        if (task.IntervalMinutes is not > 0)
            throw new InvalidOperationException("Interval schedules require a positive IntervalMinutes.");
        return nowUtc.AddMinutes(task.IntervalMinutes.Value);
    }

    private static DateTime ComputeDaily(ScheduledTask task, DateTime nowUtc)
    {
        var timeOfDay = RequireTimeOfDay(task);
        var candidate = nowUtc.Date.AddMinutes(timeOfDay);
        return candidate > nowUtc ? candidate : candidate.AddDays(1);
    }

    private static DateTime ComputeWeekly(ScheduledTask task, DateTime nowUtc)
    {
        var timeOfDay = RequireTimeOfDay(task);
        if (task.DayOfWeek is not (>= 0 and <= 6))
            throw new InvalidOperationException("Weekly schedules require DayOfWeek between 0 (Sunday) and 6 (Saturday).");

        var daysUntilTarget = (task.DayOfWeek.Value - (int)nowUtc.DayOfWeek + 7) % 7;
        var candidate = nowUtc.Date.AddDays(daysUntilTarget).AddMinutes(timeOfDay);
        return candidate > nowUtc ? candidate : candidate.AddDays(7);
    }

    private static int RequireTimeOfDay(ScheduledTask task)
    {
        if (task.TimeOfDayMinutes is not (>= 0 and <= MaxTimeOfDayMinutes))
            throw new InvalidOperationException($"Daily and weekly schedules require TimeOfDayMinutes between 0 and {MaxTimeOfDayMinutes}.");
        return task.TimeOfDayMinutes.Value;
    }
}
