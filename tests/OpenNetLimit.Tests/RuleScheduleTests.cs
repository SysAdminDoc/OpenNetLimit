using OpenNetLimit.Core.Models;
using Xunit;

namespace OpenNetLimit.Tests;

public class RuleScheduleTests
{
    // All tests pass fixed local-time DateTime values to IsActiveAt,
    // avoiding fragility near midnight or timezone-dependent UTC conversions.

    [Fact]
    public void IsActiveAt_NoSchedule_AlwaysActive()
    {
        var schedule = new RuleSchedule();
        Assert.True(schedule.IsActiveAt(DateTime.UtcNow));
    }

    [Fact]
    public void IsActiveAt_DaytimeWindow_ActiveDuringDay()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0)
        };

        // Pass a fixed local-time value that is unambiguously within the window
        var noon = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);
        Assert.True(schedule.IsActiveAt(noon));
    }

    [Fact]
    public void IsActiveAt_DaytimeWindow_InactiveAtNight()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0)
        };

        var earlyMorning = new DateTime(2025, 6, 15, 3, 0, 0, DateTimeKind.Local);
        Assert.False(schedule.IsActiveAt(earlyMorning));
    }

    [Fact]
    public void IsActiveAt_OvernightWindow_ActiveAtNight()
    {
        // 22:00 → 06:00 wraps midnight
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0)
        };

        var late = new DateTime(2025, 6, 15, 23, 0, 0, DateTimeKind.Local);
        Assert.True(schedule.IsActiveAt(late));

        var earlyMorning = new DateTime(2025, 6, 15, 2, 0, 0, DateTimeKind.Local);
        Assert.True(schedule.IsActiveAt(earlyMorning));
    }

    [Fact]
    public void IsActiveAt_OvernightWindow_InactiveDuringDay()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0)
        };

        var noon = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);
        Assert.False(schedule.IsActiveAt(noon));
    }

    [Fact]
    public void IsActiveAt_DayFilter_MatchingDay()
    {
        // Use a fixed date (Sunday June 15 2025) to avoid runtime day-of-week fragility
        var sunday = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var schedule = new RuleSchedule
        {
            ActiveDays = [DayOfWeek.Sunday]
        };

        Assert.True(schedule.IsActiveAt(sunday));
    }

    [Fact]
    public void IsActiveAt_DayFilter_NonMatchingDay()
    {
        var sunday = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var schedule = new RuleSchedule
        {
            ActiveDays = [DayOfWeek.Monday]
        };

        Assert.False(schedule.IsActiveAt(sunday));
    }

    [Fact]
    public void IsActiveAt_BoundaryExactStart()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(18, 0)
        };

        var atStart = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Local);
        Assert.True(schedule.IsActiveAt(atStart));
    }

    [Fact]
    public void IsActiveAt_BoundaryExactEnd()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(18, 0)
        };

        var atEnd = new DateTime(2025, 6, 15, 18, 0, 0, DateTimeKind.Local);
        Assert.True(schedule.IsActiveAt(atEnd));
    }
}
