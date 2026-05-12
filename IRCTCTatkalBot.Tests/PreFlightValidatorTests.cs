using IRCTCTatkalBot.Services;
using Xunit;

namespace IRCTCTatkalBot.Tests;

public class PreFlightValidatorTests
{
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
