using System.Text;
using Kkdev92.Jev.Serialization;

namespace Kkdev92.Jev.Tests;

public sealed class JsonExactIntegerTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("120", 120L)]
    [InlineData("120.0", 120L)]
    [InlineData("120.000", 120L)]
    [InlineData("1.2e2", 120L)]
    [InlineData("1.20E+2", 120L)]
    [InlineData("12000e-2", 120L)]
    [InlineData("0e99999999999999999999", 0L)]
    [InlineData("0.0e-5", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9.223372036854775807e18", long.MaxValue)]
    public void ExactIntegersAreReadInAnySpelling(string token, long expected)
    {
        Assert.True(JsonExactInteger.TryParseInt64(Encoding.ASCII.GetBytes(token), out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("120.5")]
    [InlineData("1.25e1")]
    [InlineData("12e-1")]
    [InlineData("1e-400")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("1e19")]
    [InlineData("1e400")]
    [InlineData("1e99999999999999999999")]
    public void FractionsAndValuesThatDoNotFitAreRefusedNotRounded(string token)
        => Assert.False(JsonExactInteger.TryParseInt64(Encoding.ASCII.GetBytes(token), out _));

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("+1")]
    [InlineData("1 ")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void AnythingThatIsNotAJsonNumberIsRefused(string token)
        => Assert.False(JsonExactInteger.TryParseInt64(Encoding.ASCII.GetBytes(token), out _));

    /// <summary>Through a double, 2^53 + 1 would come back as 2^53.</summary>
    [Fact]
    public void LargeCountsAreNotRoundedThroughADouble()
    {
        Assert.True(JsonExactInteger.TryParseInt64("9007199254740993"u8, out var value));
        Assert.Equal(9007199254740993L, value);
        Assert.NotEqual((long)9007199254740993.0, value);
    }
}
