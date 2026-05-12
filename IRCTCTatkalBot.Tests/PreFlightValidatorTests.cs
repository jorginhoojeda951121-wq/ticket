using System;
using IRCTCTatkalBot.Helpers;
using IRCTCTatkalBot.Services;
using Xunit;

namespace IRCTCTatkalBot.Tests;

public class PreFlightValidatorTests
{
    [Theory]
    [InlineData("3AC", true)]
    [InlineData("1AC", true)]
    [InlineData("SL", true)]
    [InlineData("3A", true)]
    [InlineData("EC", false)]
    [InlineData("XX", false)]
    public void IrctcTrainClass_IsAllowed_MatchesExpected(string cls, bool ok) =>
        Assert.Equal(ok, IrctcTrainClass.IsAllowed(cls));

    [Theory]
    [InlineData("3AC", 10, 0)]
    [InlineData("SL", 11, 0)]
    public void IrctcTrainClass_TatkalWindow(string cls, int h, int m) =>
        Assert.Equal(new TimeSpan(h, m, 0), IrctcTrainClass.TatkalWindowOpen(cls));

    [Theory]
    [InlineData("12215", true)]
    [InlineData("12951", true)]
    [InlineData("123456", true)]
    [InlineData("1234", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("abc", false)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    public void IsValidIrctcTrainNumber_MatchesExpected(string input, bool expected) =>
        Assert.Equal(expected, PreFlightValidator.IsValidIrctcTrainNumber(input));
}
