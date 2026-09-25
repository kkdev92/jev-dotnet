using System.Net;
using System.Text;
using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>What each kind of failure becomes, and what its message is allowed to say.</summary>
public sealed class ErrorContractTests
{
    private const string StateSecret = "state-secret-4111111111111111";

    private static async Task<TException> Fails<TException>(HttpMessageHandler handler, Action<JevClientOptions>? configure = null, JevRequestOptions? options = null)
        where TException : Exception
    {
        var (plan, _, _) = Plan();
        return await Assert.ThrowsAsync<TException>(() => Create(handler, configure).EvaluateAsync(StateSecret, plan, options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingKeyAnswersForbiddenAndIsReportedWithItsErrorType()
    {
        // Observed rather than documented (missing-key-status in semantics.json): no key is 403, not
        // the 401 the HTTP reference documents.
        var exception = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.MissingKey())));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("authentication_error", exception.ErrorType);
        Assert.Equal(FakeResponses.RequestId, exception.RequestId);
        Assert.Equal(JevRequestTransmission.Started, exception.Transmission);
        Assert.Equal(JevOperation.Evaluate, exception.Operation);
        Assert.Contains("403 (Forbidden)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("authentication_error", exception.Message, StringComparison.Ordinal);
        Assert.Contains(FakeResponses.RequestId, exception.Message, StringComparison.Ordinal);

        // The server's own words stay out of the message.
        Assert.DoesNotContain("Must supply", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.ErrorBody);
    }

    [Fact]
    public async Task AnInvalidKeyIsUnauthorized()
    {
        var exception = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.InvalidKey())));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.IsRateLimited);
    }

    [Fact]
    public async Task AnUnknownModelIsABadRequestNamedByItsErrorType()
    {
        var exception = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.UnknownModel())));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("api_usage_error", exception.ErrorType);
        Assert.Contains("api_usage_error", exception.Message, StringComparison.Ordinal);

        // The server's sentence names the model, which the caller chose; it stays out.
        Assert.DoesNotContain("jev-does-not-exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidationFailureCarriesItsCodesButNeverTheEchoedInput()
    {
        var exception = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.ValidationFailed(StateSecret))));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(["too_short", "missing"], exception.ValidationErrors.Select(e => e.Type));
        Assert.Equal(["body", "questions", "urgency", "criteria"], exception.ValidationErrors[0].Location);
        Assert.Contains("Validation error types: too_short, missing.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StateSecret, exception.ToString(), StringComparison.Ordinal);

        // The location names a question id, which is the caller's and may be sensitive: not in the message.
        Assert.DoesNotContain("urgency", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheErrorBodyIsKeptOnlyWhenAskedAndOnlyUpToTheLimit()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.ValidationFailed()));

        var captured = await Fails<JevHttpException>(handler, options: new JevRequestOptions { CaptureErrorBody = true });
        Assert.Contains("too_short", Encoding.UTF8.GetString(captured.ErrorBody!.Value.Span), StringComparison.Ordinal);

        var truncated = await Fails<JevHttpException>(handler, o => o.MaxErrorBodyBytes = 16, new JevRequestOptions { CaptureErrorBody = true });
        Assert.Equal(16, truncated.ErrorBody!.Value.Length);

        // Truncated means unparseable, and the status still stands.
        Assert.Equal(422, truncated.StatusCode);
        Assert.Empty(truncated.ValidationErrors);
    }

    [Fact]
    public async Task AnHtmlErrorPageStillReportsItsStatus()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Json("<html>502 Bad Gateway</html>", HttpStatusCode.BadGateway)));
        var exception = await Fails<JevHttpException>(handler);

        Assert.Equal(502, exception.StatusCode);
        Assert.Null(exception.ErrorType);
    }

    [Fact]
    public async Task AnUnknownStatusIsKept()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Json("{}", (HttpStatusCode)599)));
        var exception = await Fails<JevHttpException>(handler);

        Assert.Equal(599, exception.StatusCode);
        Assert.StartsWith("The TypeSafe API returned 599 for POST /v1/systemone.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverloadAndRateLimitingAreRecognised()
    {
        var overloaded = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Overloaded())));
        var limited = await Fails<JevHttpException>(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.RateLimited(retryAfter: "12"))));

        Assert.True(overloaded.IsOverloaded);
        Assert.Contains("529 (Overloaded)", overloaded.Message, StringComparison.Ordinal);
        Assert.True(limited.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(12), limited.RetryAfter);
    }

    [Fact]
    public async Task ASuccessfulResponseThatBreaksTheContractIsAProtocolError()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("urgent", 0.5).Build());

        var exception = await Assert.ThrowsAsync<JevProtocolException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevProtocolError.MissingAnswer, exception.Error);
        Assert.Equal(FakeResponses.RequestId, exception.RequestId);
        Assert.Equal(JevRequestTransmission.Started, exception.Transmission);
    }

    /// <summary>The JSON reader does not validate UTF-8 it skips, so the client checks the whole body first.</summary>
    [Fact]
    public async Task InvalidUtf8AnywhereInTheBodyIsRefused()
    {
        var (plan, _, _) = Plan();
        byte[] body = [.. Encoding.UTF8.GetBytes(Answer()[..^1]), .. ",\"ignored\":\""u8, 0xC3, 0x28, .. "\"}"u8];
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Bytes(body)));

        var exception = await Assert.ThrowsAsync<JevProtocolException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevProtocolError.InvalidUtf8, exception.Error);
    }

    [Fact]
    public async Task StrictModeRefusesInconsistentNumbersAndReportModeReturnsThem()
    {
        var (plan, _, _) = Plan();
        var inconsistent = new SystemOneResponseBuilder()
            .Choice("tone", "calm", 0.76, ("calm", 0.3), ("frustrated", 0.5), ("angry", 0.2))
            .Noul("urgent", 0.5)
            .Build();
        var handler = FakeHttpMessageHandler.Always(inconsistent);

        var reported = await Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        var warning = Assert.Single(reported.Metadata.Warnings);
        Assert.Equal(JevConsistencyCheck.ChoiceIsMostProbable, warning.Check);
        Assert.Equal("tone", warning.QuestionId);

        var strict = await Assert.ThrowsAsync<JevProtocolException>(() => Create(handler, o => o.NumericalConsistency = JevNumericalConsistency.Strict)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevProtocolError.NumericalInconsistency, strict.Error);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimitIsRefusedBeforeReading()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer() + new string(' ', 2048));

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(handler, o => o.MaxResponseBodyBytes = 1024)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.ResponseBody, exception.Limit);
        Assert.Equal(1024, exception.LimitValue);
        Assert.Equal(JevRequestTransmission.Started, exception.Transmission);
    }

    [Fact]
    public async Task AnUnsizedBodyIsCountedAsItArrives()
    {
        var (plan, _, _) = Plan();
        var answer = Encoding.UTF8.GetBytes(Answer());
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent([.. answer, .. new byte[2048].AsSpan().ToArray().Select(_ => (byte)' ')]) }));

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(handler, o => o.MaxResponseBodyBytes = 1024)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevLimit.ResponseBody, exception.Limit);

        // Exactly at the limit is not over it, and one byte more is. JSON allows trailing
        // whitespace, so the answer is padded to the byte.
        var exact = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(PadTo(answer, 1024)) }));
        await Create(exact, o => o.MaxResponseBodyBytes = 1024).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        var oneOver = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(PadTo(answer, 1025)) }));
        await Assert.ThrowsAsync<JevLimitException>(() => Create(oneOver, o => o.MaxResponseBodyBytes = 1024)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The same boundary when the body says how long it is.</summary>
    [Fact]
    public async Task ASizedBodyAtTheLimitIsReadAndOneByteOverIsNot()
    {
        var (plan, _, _) = Plan();
        var answer = Encoding.UTF8.GetBytes(Answer());

        var exact = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PadTo(answer, 1024)) }));
        await Create(exact, o => o.MaxResponseBodyBytes = 1024).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        var oneOver = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PadTo(answer, 1025)) }));
        await Assert.ThrowsAsync<JevLimitException>(() => Create(oneOver, o => o.MaxResponseBodyBytes = 1024)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static byte[] PadTo(byte[] json, int length)
    {
        Assert.True(json.Length <= length, "The answer is longer than the padding target.");
        return [.. json, .. Enumerable.Repeat((byte)' ', length - json.Length)];
    }

    /// <summary>A <c>Content-Length</c> that understates the body sizes the first buffer, and is trusted for nothing else.</summary>
    [Fact]
    public async Task AContentLengthThatUnderstatesTheBodyIsNotTrusted()
    {
        var (plan, tone, _) = Plan();
        var answer = Encoding.UTF8.GetBytes(Answer());

        // Declared as 16 bytes and delivered in full: the reader keeps reading past the declaration.
        var fits = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new MisdeclaredContent(answer, declared: 16) }));
        var result = await Create(fits).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Tone.Frustrated, result.Get(tone).Value);

        // And refuses past the limit, however little was declared.
        byte[] padded = [.. answer, .. Enumerable.Repeat((byte)' ', 2048)];
        var over = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new MisdeclaredContent(padded, declared: 16) }));

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(over, o => o.MaxResponseBodyBytes = 1024)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.ResponseBody, exception.Limit);
    }

    /// <summary>A state under the limit inside a body over it is caught while writing, not by the early check on the state alone.</summary>
    [Fact]
    public async Task AStateThatFitsInABodyThatDoesNotIsNeverSent()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer());

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(handler, o => o.MaxRequestBodyBytes = 2048)
            .EvaluateAsync(new string('x', 2000), plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.RequestBody, exception.Limit);
        Assert.Equal(JevRequestTransmission.NotStarted, exception.Transmission);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARequestOverTheLimitIsNeverSent()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer());

        var exception = await Assert.ThrowsAsync<JevLimitException>(() => Create(handler, o => o.MaxRequestBodyBytes = 2048)
            .EvaluateAsync(new string('x', 4096), plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.RequestBody, exception.Limit);
        Assert.Equal(JevRequestTransmission.NotStarted, exception.Transmission);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AConnectionThatCannotBeMadeSentNothing()
    {
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var exception = await Assert.ThrowsAsync<JevTransportException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevTransportFailure.Connect, exception.Failure);
        Assert.Equal(JevRequestTransmission.NotStarted, exception.Transmission);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task AFailureMidExchangeHasAnUnknownOutcome()
    {
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "reset"));

        var exception = await Assert.ThrowsAsync<JevTransportException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevTransportFailure.Unknown, exception.Failure);
        Assert.Equal(JevRequestTransmission.Unknown, exception.Transmission);
    }

    [Fact]
    public async Task HttpClientsOwnTimeoutIsATransportFailureNotACancellation()
    {
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException()));

        var exception = await Assert.ThrowsAsync<JevTransportException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevTransportFailure.HttpClientTimeout, exception.Failure);
    }

    [Fact]
    public async Task AKeyThatCannotBeSentStopsTheCallBeforeAnythingIsSent()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer());
        var credential = new DelegateCredential(_ => "sk live\r\nX-Injected: 1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(handler, o => o.Credential = credential)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("sk live", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoFailureMessageRepeatsTheKeyTheStateOrAnId()
    {
        var (plan, _, _) = Plan();
        var failures = new Func<HttpResponseMessage>[]
        {
            FakeResponses.MissingKey,
            FakeResponses.InvalidKey,
            () => FakeResponses.ValidationFailed(StateSecret),
            () => FakeResponses.RateLimited("5"),
            FakeResponses.Overloaded,
            () => FakeResponses.Json("{\"model\":\"" + StateSecret + "\"}"),
            () => FakeResponses.Json(Answer().Replace("\"frustrated\":0.84", "\"" + StateSecret + "\":0.84", StringComparison.Ordinal)),
        };

        foreach (var failure in failures)
        {
            var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(failure()));
            var exception = await Assert.ThrowsAnyAsync<JevException>(() => Create(handler).EvaluateAsync(StateSecret, plan, cancellationToken: TestContext.Current.CancellationToken));

            foreach (var text in new[] { exception.Message, exception.ToString() })
            {
                Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
                Assert.DoesNotContain(StateSecret, text, StringComparison.Ordinal);
                Assert.DoesNotContain("tone", text, StringComparison.Ordinal);
            }
        }
    }

    internal sealed class DelegateCredential(Func<int, string> key) : IJevCredential
    {
        private int _calls;

        public int Calls => _calls;

        public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(key(Interlocked.Increment(ref _calls)));
    }
}
