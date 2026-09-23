using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Transport;

namespace Kkdev92.Jev.Tests;

public sealed class OptionsTests
{
    private static JevClientOptions Valid() => new() { Credential = new StaticJevCredential("sk-test") };

    [Fact]
    public void TheDefaultsAreTheDocumentedOnes()
    {
        var options = new JevClientOptions();

        Assert.Equal("jev-latest", options.DefaultModel);
        Assert.Equal(new Uri("https://api.typesafe.ai"), options.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(0, options.AdditionalRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RetryBackoffInitial);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryBackoffMaximum);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaximumServerRetryWait);
        Assert.False(options.RetryNetworkFailures);
        Assert.Equal(4 * 1024 * 1024, options.MaxRequestBodyBytes);
        Assert.Equal(4 * 1024 * 1024, options.MaxResponseBodyBytes);
        Assert.Equal(64 * 1024, options.MaxErrorBodyBytes);
        Assert.Null(options.ConcurrencyLimit);
        Assert.Equal(0, options.MaxQueuedRequests);
        Assert.False(options.CaptureRawResponse);
        Assert.False(options.CaptureErrorBody);
        Assert.Equal(JevNumericalConsistency.Report, options.NumericalConsistency);
        Assert.Equal(1e-6, options.NumericalAbsoluteTolerance);
        Assert.Equal(1e-6, options.NumericalRelativeTolerance);
        Assert.Same(TimeProvider.System, options.TimeProvider);
    }

    [Fact]
    public void ACredentialIsRequired()
    {
        var exception = Assert.Throws<ArgumentException>(() => ClientSettings.From(new JevClientOptions()));
        Assert.Contains("Credential", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, Action<JevClientOptions>> Invalid => new()
    {
        { "empty model", o => o.DefaultModel = "" },
        { "http", o => o.BaseAddress = new Uri("http://api.typesafe.ai") },
        { "path", o => o.BaseAddress = new Uri("https://proxy.example/typesafe/") },
        { "query", o => o.BaseAddress = new Uri("https://api.typesafe.ai/?key=x") },
        { "user info", o => o.BaseAddress = new Uri("https://user:secret@api.typesafe.ai") },
        { "zero timeout", o => o.Timeout = TimeSpan.Zero },
        { "negative timeout", o => o.Timeout = TimeSpan.FromSeconds(-1) },
        { "negative retries", o => o.AdditionalRetries = -1 },
        { "too many retries", o => o.AdditionalRetries = 11 },
        { "zero backoff", o => o.RetryBackoffInitial = TimeSpan.Zero },
        { "backoff above maximum", o => o.RetryBackoffInitial = TimeSpan.FromSeconds(10) },
        { "negative server wait", o => o.MaximumServerRetryWait = TimeSpan.FromSeconds(-1) },
        { "tiny request limit", o => o.MaxRequestBodyBytes = 100 },
        { "tiny response limit", o => o.MaxResponseBodyBytes = 100 },
        { "negative error limit", o => o.MaxErrorBodyBytes = -1 },
        { "zero concurrency", o => o.ConcurrencyLimit = 0 },
        { "negative queue", o => { o.ConcurrencyLimit = 1; o.MaxQueuedRequests = -1; } },
        { "queue without a limit", o => o.MaxQueuedRequests = 5 },
        { "undefined mode", o => o.NumericalConsistency = (JevNumericalConsistency)9 },
        { "NaN tolerance", o => o.NumericalAbsoluteTolerance = double.NaN },
        { "tolerance of one", o => o.NumericalRelativeTolerance = 1 },
        { "no clock", o => o.TimeProvider = null! },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidOptionsAreRefusedWhenTheClientIsCreated(string because, Action<JevClientOptions> breakIt)
    {
        var options = Valid();
        breakIt(options);

        Assert.ThrowsAny<ArgumentException>(() => new JevClient(new HttpClient(), options));
        Assert.False(string.IsNullOrEmpty(because));
    }

    [Fact]
    public void AnInfiniteTimeoutIsAllowed()
    {
        var options = Valid();
        options.Timeout = Timeout.InfiniteTimeSpan;

        _ = new JevClient(new HttpClient(), options);
    }

    [Fact]
    public void TheBaseAddressIsNeverEchoed()
    {
        var options = Valid();
        options.BaseAddress = new Uri("https://user:hunter2@api.typesafe.ai");

        var exception = Assert.Throws<ArgumentException>(() => new JevClient(new HttpClient(), options));
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAreCopiedWhenTheClientIsCreated()
    {
        var options = Valid();
        var settings = ClientSettings.From(options);

        options.DefaultModel = "changed";
        options.Timeout = TimeSpan.FromSeconds(1);

        Assert.Equal("jev-latest", settings.DefaultModel);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
    }

    [Fact]
    public void PerCallOptionsOverrideAndZeroIsAValue()
    {
        var options = Valid();
        options.AdditionalRetries = 3;
        var settings = ClientSettings.From(options);

        var call = CallSettings.Resolve(settings, new JevRequestOptions { AdditionalRetries = 0, Model = "jev-1.13.0", CaptureRawResponse = true });

        Assert.Equal(0, call.AdditionalRetries);
        Assert.Equal("jev-1.13.0", call.Model);
        Assert.True(call.CaptureRawResponse);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        Assert.Same(settings.Credential, call.Credential);
    }

    [Fact]
    public void InvalidPerCallOptionsAreRefusedBeforeTheCallStarts()
    {
        var settings = ClientSettings.From(Valid());

        Assert.ThrowsAny<ArgumentException>(() => CallSettings.Resolve(settings, new JevRequestOptions { Model = "" }));
        Assert.ThrowsAny<ArgumentException>(() => CallSettings.Resolve(settings, new JevRequestOptions { Timeout = TimeSpan.Zero }));
        Assert.ThrowsAny<ArgumentException>(() => CallSettings.Resolve(settings, new JevRequestOptions { AdditionalRetries = 99 }));
    }

    [Theory]
    [InlineData(0, 0.0, 0)]
    [InlineData(0, 0.999, 249)]
    [InlineData(1, 0.5, 250)]
    [InlineData(2, 1.0, 1000)]
    [InlineData(10, 1.0, 5000)]
    [InlineData(1000, 1.0, 5000)]
    public void BackoffIsFullJitterUnderACappedDoublingCeiling(int retry, double draw, int expectedMilliseconds)
    {
        var delay = RetryRules.Backoff(retry, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), draw);
        Assert.Equal(expectedMilliseconds, (int)delay.TotalMilliseconds);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(529, true)]
    [InlineData(500, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(422, false)]
    public void OnlyTheAllowlistedStatusesAreRetryable(int status, bool retryable)
        => Assert.Equal(retryable, RetryRules.IsRetryableStatus(status));
}
