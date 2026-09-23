using System.Net;
using Kkdev92.Jev.Responses;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.Jev.Tests;

public sealed class ResponseHeaderTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));

    private static TimeSpan? RetryAfter(params (string Name, string Value)[] headers)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return ResponseHeaders.RetryAfter(response.Headers, Clock);
    }

    [Fact]
    public void DeltaSecondsAreRead() => Assert.Equal(TimeSpan.FromSeconds(7), RetryAfter(("Retry-After", "7")));

    [Fact]
    public void AnHttpDateIsMeasuredFromTheInjectedClock()
        => Assert.Equal(TimeSpan.FromSeconds(90), RetryAfter(("Retry-After", "Wed, 23 Sep 2026 00:01:30 GMT")));

    [Fact]
    public void ADateInThePastMeansNow()
        => Assert.Equal(TimeSpan.Zero, RetryAfter(("Retry-After", "Tue, 22 Sep 2026 23:00:00 GMT")));

    [Fact]
    public void MillisecondsWinOverSeconds()
        => Assert.Equal(TimeSpan.FromMilliseconds(1500), RetryAfter(("Retry-After", "30"), ("retry-after-ms", "1500")));

    [Fact]
    public void FractionalMillisecondsAreKept()
        => Assert.Equal(TimeSpan.FromMilliseconds(250.5), RetryAfter(("retry-after-ms", "250.5")));

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("NaN")]
    [InlineData("1e400")]
    public void AnUnreadableMillisecondValueFallsBackToRetryAfter(string value)
        => Assert.Equal(TimeSpan.FromSeconds(3), RetryAfter(("retry-after-ms", value), ("Retry-After", "3")));

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    public void AnUnreadableRetryAfterIsAbsentNotZero(string value)
        => Assert.Null(RetryAfter(("Retry-After", value)));

    [Fact]
    public void NoHeaderIsNoInstruction() => Assert.Null(RetryAfter());

    /// <summary>
    /// A wait the usual reading refuses is still a wait. Read as absent, it would hand the call to
    /// the SDK's own backoff, which is seconds: a retry long before the server said to.
    /// </summary>
    [Theory]
    [InlineData("retry-after-ms", "172800000", 172_800_000d)]
    [InlineData("Retry-After", "99999999999", 99_999_999_999_000d)]
    [InlineData("Retry-After", "1.5", 1_500d)]
    public void AWaitTheHeaderParserRefusesIsStillAWait(string header, string value, double milliseconds)
        => Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), RetryAfter((header, value)));

    [Theory]
    [InlineData("retry-after-ms")]
    [InlineData("Retry-After")]
    public void AWaitLongerThanATimeSpanReadsAsTheLongestOne(string header)
        => Assert.Equal(TimeSpan.MaxValue, RetryAfter((header, "1000000000000000000000")));

    [Theory]
    [InlineData("req_01a0caf71f397499b5c738884f2ce455", true)]
    [InlineData("abc-DEF_123", true)]
    [InlineData("has space", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("<script>", false)]
    [InlineData("", false)]
    public void ARequestIdIsKeptOnlyInTheShapeOfAnIdentifier(string value, bool kept)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-typesafe-request-id", value);

        Assert.Equal(kept ? value : null, ResponseHeaders.RequestId(response));
    }

    [Fact]
    public void AnOverlongOrRepeatedRequestIdIsDropped()
    {
        using var overlong = new HttpResponseMessage(HttpStatusCode.OK);
        overlong.Headers.TryAddWithoutValidation("x-typesafe-request-id", new string('a', 129));
        Assert.Null(ResponseHeaders.RequestId(overlong));

        using var repeated = new HttpResponseMessage(HttpStatusCode.OK);
        repeated.Headers.TryAddWithoutValidation("x-typesafe-request-id", ["req_a", "req_b"]);
        Assert.Null(ResponseHeaders.RequestId(repeated));
    }
}
