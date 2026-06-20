using OpenNetLimit.Core.Models;
using Xunit;

namespace OpenNetLimit.Tests;

public class RuleScheduleTests
{
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

        // Use local time directly since IsActiveAt converts UTC→local
        var localNoon = DateTime.Today.AddHours(12);
        var utcNoon = localNoon.ToUniversalTime();
        Assert.True(schedule.IsActiveAt(utcNoon));
    }

    [Fact]
    public void IsActiveAt_DaytimeWindow_InactiveAtNight()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0)
        };

        var local3am = DateTime.Today.AddHours(3);
        var utc3am = local3am.ToUniversalTime();
        Assert.False(schedule.IsActiveAt(utc3am));
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

        var local23 = DateTime.Today.AddHours(23);
        Assert.True(schedule.IsActiveAt(local23.ToUniversalTime()));

        var local2am = DateTime.Today.AddHours(2);
        Assert.True(schedule.IsActiveAt(local2am.ToUniversalTime()));
    }

    [Fact]
    public void IsActiveAt_OvernightWindow_InactiveDuringDay()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0)
        };

        var localNoon = DateTime.Today.AddHours(12);
        Assert.False(schedule.IsActiveAt(localNoon.ToUniversalTime()));
    }

    [Fact]
    public void IsActiveAt_DayFilter_MatchingDay()
    {
        var today = DateTime.Now.DayOfWeek;
        var schedule = new RuleSchedule
        {
            ActiveDays = [today]
        };

        Assert.True(schedule.IsActiveAt(DateTime.UtcNow));
    }

    [Fact]
    public void IsActiveAt_DayFilter_NonMatchingDay()
    {
        var tomorrow = (DayOfWeek)(((int)DateTime.Now.DayOfWeek + 1) % 7);
        var schedule = new RuleSchedule
        {
            ActiveDays = [tomorrow]
        };

        Assert.False(schedule.IsActiveAt(DateTime.UtcNow));
    }

    [Fact]
    public void IsActiveAt_BoundaryExactStart()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(18, 0)
        };

        var localAt10 = DateTime.Today.AddHours(10);
        Assert.True(schedule.IsActiveAt(localAt10.ToUniversalTime()));
    }

    [Fact]
    public void IsActiveAt_BoundaryExactEnd()
    {
        var schedule = new RuleSchedule
        {
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(18, 0)
        };

        var localAt18 = DateTime.Today.AddHours(18);
        Assert.True(schedule.IsActiveAt(localAt18.ToUniversalTime()));
    }
}
