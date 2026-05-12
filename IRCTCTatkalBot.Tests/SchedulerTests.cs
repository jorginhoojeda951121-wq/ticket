using System;
using IRCTCTatkalBot.Services;
using Xunit;

namespace IRCTCTatkalBot.Tests;

public class SchedulerTests
{
    [Theory]
    [InlineData("SL", 11, 0)]
    [InlineData("2S", 11, 0)]
    [InlineData("1AC", 10, 0)]
    [InlineData("2AC", 10, 0)]
    [InlineData("3AC", 10, 0)]
    [InlineData("3A", 10, 0)]
    [InlineData("2A", 10, 0)]
    [InlineData("1A", 10, 0)]
    [InlineData("CC", 10, 0)]
    public void GetWindowForClass_ReturnsExpectedIstHour(string trainClass, int hour, int minute)
    {
        var expected = new TimeSpan(hour, minute, 0);
        var actual = Scheduler.GetWindowForClass(trainClass);
        Assert.Equal(expected, actual);
    }

}
