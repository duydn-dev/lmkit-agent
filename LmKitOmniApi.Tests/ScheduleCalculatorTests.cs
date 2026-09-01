using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Tests;

public class ScheduleCalculatorTests
{
    // Wednesday (DayOfWeek 3), 10:00 UTC. All expectations below are anchored to this instant.
    private static readonly DateTime Now = new(2026, 1, 7, 10, 0, 0, DateTimeKind.Utc);

    private static ScheduledTask Interval(int? minutes) => new()
    {
        ScheduleKind = "interval",
        IntervalMinutes = minutes
    };

    private static ScheduledTask Daily(int? timeOfDayMinutes) => new()
    {
        ScheduleKind = "daily",
        TimeOfDayMinutes = timeOfDayMinutes
    };

    private static ScheduledTask Weekly(int? dayOfWeek, int? timeOfDayMinutes) => new()
    {
        ScheduleKind = "weekly",
        DayOfWeek = dayOfWeek,
        TimeOfDayMinutes = timeOfDayMinutes
    };

    [Fact]
    public void Sanity_AnchorIsWednesday()
    {
        Assert.Equal(DayOfWeek.Wednesday, Now.DayOfWeek);
    }

    // ---------------------------------------------------------------- interval

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(10080)]
    public void Interval_AddsIntervalMinutesToNow(int minutes)
    {
        Assert.Equal(Now.AddMinutes(minutes), ScheduleCalculator.ComputeNextRun(Interval(minutes), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Interval_MissingOrNonPositiveMinutes_Throws(int? minutes)
    {
        Assert.Throws<InvalidOperationException>(() => ScheduleCalculator.ComputeNextRun(Interval(minutes), Now));
    }

    // ---------------------------------------------------------------- daily

    [Fact]
    public void Daily_TimeStillAheadToday_RunsToday()
    {
        var next = ScheduleCalculator.ComputeNextRun(Daily(601), Now); // 10:01 UTC
        Assert.Equal(new DateTime(2026, 1, 7, 10, 1, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_ExactlyNow_RollsToNextDay()
    {
        var next = ScheduleCalculator.ComputeNextRun(Daily(600), Now); // exactly 10:00 UTC
        Assert.Equal(new DateTime(2026, 1, 8, 10, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_TimeAlreadyPassedToday_RollsToNextDay()
    {
        var next = ScheduleCalculator.ComputeNextRun(Daily(599), Now); // 09:59 UTC
        Assert.Equal(new DateTime(2026, 1, 8, 9, 59, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_MidnightAtMidnight_RollsToNextMidnight()
    {
        var midnight = new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        var next = ScheduleCalculator.ComputeNextRun(Daily(0), midnight);
        Assert.Equal(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_LastMinuteOfDay_RunsToday()
    {
        var next = ScheduleCalculator.ComputeNextRun(Daily(1439), Now); // 23:59 UTC
        Assert.Equal(new DateTime(2026, 1, 7, 23, 59, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(1440)]
    public void Daily_MissingOrOutOfRangeTimeOfDay_Throws(int? timeOfDayMinutes)
    {
        Assert.Throws<InvalidOperationException>(() => ScheduleCalculator.ComputeNextRun(Daily(timeOfDayMinutes), Now));
    }

    // ---------------------------------------------------------------- weekly

    [Fact]
    public void Weekly_SameDayTimeStillAhead_RunsToday()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(3, 601), Now); // Wednesday 10:01
        Assert.Equal(new DateTime(2026, 1, 7, 10, 1, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_ExactlyNow_RollsToNextWeek()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(3, 600), Now); // Wednesday 10:00, exactly now
        Assert.Equal(new DateTime(2026, 1, 14, 10, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_SameDayTimeAlreadyPassed_RollsToNextWeek()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(3, 599), Now); // Wednesday 09:59
        Assert.Equal(new DateTime(2026, 1, 14, 9, 59, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_LaterDayThisWeek_RunsThisWeek()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(5, 0), Now); // Friday 00:00
        Assert.Equal(new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal(DayOfWeek.Friday, next.DayOfWeek);
    }

    [Fact]
    public void Weekly_EarlierDayOfWeek_WrapsToNextWeek()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(1, 600), Now); // Monday 10:00
        Assert.Equal(new DateTime(2026, 1, 12, 10, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void Weekly_SundayZeroBoundary_Works()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(0, 1439), Now); // Sunday 23:59
        Assert.Equal(new DateTime(2026, 1, 11, 23, 59, 0, DateTimeKind.Utc), next);
        Assert.Equal(DayOfWeek.Sunday, next.DayOfWeek);
    }

    [Fact]
    public void Weekly_SaturdaySixBoundary_Works()
    {
        var next = ScheduleCalculator.ComputeNextRun(Weekly(6, 0), Now); // Saturday 00:00
        Assert.Equal(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal(DayOfWeek.Saturday, next.DayOfWeek);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(-1, 600)]
    [InlineData(7, 600)]
    [InlineData(3, null)]
    [InlineData(3, -1)]
    [InlineData(3, 1440)]
    public void Weekly_MissingOrOutOfRangeFields_Throws(int? dayOfWeek, int? timeOfDayMinutes)
    {
        Assert.Throws<InvalidOperationException>(
            () => ScheduleCalculator.ComputeNextRun(Weekly(dayOfWeek, timeOfDayMinutes), Now));
    }

    // ---------------------------------------------------------------- kind handling

    [Theory]
    [InlineData("cron")]
    [InlineData("monthly")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownKind_Throws(string kind)
    {
        var task = new ScheduledTask { ScheduleKind = kind, IntervalMinutes = 30, TimeOfDayMinutes = 600, DayOfWeek = 1 };
        Assert.Throws<InvalidOperationException>(() => ScheduleCalculator.ComputeNextRun(task, Now));
    }

    [Fact]
    public void NullKind_Throws()
    {
        var task = new ScheduledTask { ScheduleKind = null!, IntervalMinutes = 30 };
        Assert.Throws<InvalidOperationException>(() => ScheduleCalculator.ComputeNextRun(task, Now));
    }

    [Fact]
    public void NullTask_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScheduleCalculator.ComputeNextRun(null!, Now));
    }

    [Fact]
    public void Kind_IsCaseAndWhitespaceInsensitive()
    {
        var task = new ScheduledTask { ScheduleKind = "  Daily ", TimeOfDayMinutes = 601 };
        var next = ScheduleCalculator.ComputeNextRun(task, Now);
        Assert.Equal(new DateTime(2026, 1, 7, 10, 1, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Result_PreservesUtcKind()
    {
        Assert.Equal(DateTimeKind.Utc, ScheduleCalculator.ComputeNextRun(Interval(15), Now).Kind);
        Assert.Equal(DateTimeKind.Utc, ScheduleCalculator.ComputeNextRun(Daily(0), Now).Kind);
        Assert.Equal(DateTimeKind.Utc, ScheduleCalculator.ComputeNextRun(Weekly(0, 0), Now).Kind);
    }
}
