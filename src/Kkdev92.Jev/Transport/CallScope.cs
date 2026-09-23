using System.Diagnostics;
using Kkdev92.Jev.Diagnostics;
using Kkdev92.Jev.Internal;

namespace Kkdev92.Jev.Transport;

/// <summary>
/// Everything one call owns from start to finish: its deadline, its admission permit, its attempt
/// count, its telemetry, and the rules for turning a failure into the exception the caller sees.
/// </summary>
/// <remarks>
/// <para>
/// The deadline is one <see cref="CancellationTokenSource"/> driven by the configured
/// <see cref="TimeProvider"/>, started when the call starts and linked to the caller's token. Every
/// awaited step — admission, the credential, each send, each body read, each backoff — observes the
/// linked token, so the budget covers all of them and a retry never resets it. When the call needs
/// neither (no timeout, a token that cannot be cancelled) no source is created at all.
/// </para>
/// <para>
/// A failure is classified in a fixed order: the caller's cancellation first, then the SDK's
/// deadline, then whatever the transport reported. So a call cancelled by its caller surfaces as
/// <see cref="OperationCanceledException"/> carrying the caller's token even if the deadline fired
/// in the same instant, and only a genuine deadline becomes <see cref="JevTimeoutException"/>.
/// </para>
/// </remarks>
internal sealed class CallScope : IDisposable
{
    private readonly ClientSettings _settings;
    private readonly AdmissionGate? _gate;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource? _deadline;
    private readonly CancellationTokenSource? _linked;
    private readonly long _started;
    private readonly Activity? _activity;
    private bool _admitted;
    private string _outcome = JevTelemetry.Outcomes.Error;

    public CallScope(ClientSettings settings, AdmissionGate? gate, JevOperation operation, CallSettings call, CancellationToken callerToken)
    {
        _settings = settings;
        _gate = gate;
        _callerToken = callerToken;
        Operation = operation;
        Call = call;
        _started = settings.TimeProvider.GetTimestamp();

        if (call.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            _deadline = new CancellationTokenSource(call.Timeout, settings.TimeProvider);

            if (callerToken.CanBeCanceled)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _deadline.Token);
                Token = _linked.Token;
            }
            else
            {
                Token = _deadline.Token;
            }
        }
        else
        {
            Token = callerToken;
        }

        _activity = JevTelemetry.Start(operation);

        if (JevTelemetry.Active.Enabled)
        {
            JevTelemetry.Active.Add(1, new KeyValuePair<string, object?>(JevTelemetry.OperationTag, Operations.TagValue(operation)));
        }
    }

    public JevOperation Operation { get; }

    public CallSettings Call { get; }

    /// <summary>The token every step of the call observes: the caller's, linked with the deadline.</summary>
    public CancellationToken Token { get; }

    /// <summary>HTTP requests made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>How far the current attempt got, for an exception thrown now.</summary>
    public JevRequestTransmission Transmission { get; set; } = JevRequestTransmission.NotStarted;

    public TimeSpan Elapsed => _settings.TimeProvider.GetElapsedTime(_started);

    public async ValueTask AdmitAsync()
    {
        if (_gate is null)
        {
            return;
        }

        var waitStarted = _settings.TimeProvider.GetTimestamp();
        var waited = await _gate.EnterAsync(Operation, Token).ConfigureAwait(false);
        _admitted = true;

        if (waited && JevTelemetry.QueueDuration.Enabled)
        {
            JevTelemetry.QueueDuration.Record(
                _settings.TimeProvider.GetElapsedTime(waitStarted).TotalSeconds,
                new KeyValuePair<string, object?>(JevTelemetry.OperationTag, Operations.TagValue(Operation)));
        }
    }

    /// <summary>
    /// Whether the failed attempt is retried, and after how long. Null means it is not.
    /// </summary>
    /// <param name="networkFailure">The attempt failed without a response, so its outcome is unknown.</param>
    /// <param name="serverWait">What the response asked for, from <c>retry-after-ms</c> or <c>Retry-After</c>.</param>
    public TimeSpan? DecideRetry(bool networkFailure, TimeSpan? serverWait)
    {
        if (Attempts > Call.AdditionalRetries)
        {
            return null;
        }

        if (networkFailure && !_settings.RetryNetworkFailures)
        {
            return null;
        }

        TimeSpan delay;

        if (serverWait is { } wait)
        {
            // Honoured or declined, never shortened: retrying before the server said it would be
            // ready arrives at a server that is not ready.
            if (wait > _settings.MaximumServerRetryWait)
            {
                return null;
            }

            delay = wait;
        }
        else
        {
            delay = RetryRules.Backoff(Attempts - 1, _settings.RetryBackoffInitial, _settings.RetryBackoffMaximum, _settings.Jitter());
        }

        // A retry that could not start before the deadline would spend the rest of the budget
        // waiting and then fail anyway, with a less useful error than the one in hand.
        if (Call.Timeout != System.Threading.Timeout.InfiniteTimeSpan && delay >= Call.Timeout - Elapsed)
        {
            return null;
        }

        return delay;
    }

    public async Task DelayAsync(TimeSpan delay)
    {
        if (JevTelemetry.Retries.Enabled)
        {
            JevTelemetry.Retries.Add(1, new KeyValuePair<string, object?>(JevTelemetry.OperationTag, Operations.TagValue(Operation)));
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _settings.TimeProvider, Token).ConfigureAwait(false);
        }
    }

    /// <summary>A result is never returned after its deadline, however close the call came.</summary>
    /// <remarks>
    /// The elapsed time is checked as well as the token, because the timer that cancels the token
    /// can run late. Past the deadline the caller's cancellation is still reported first, as
    /// <see cref="Classify"/> reports every other failure.
    /// </remarks>
    public void ThrowIfDeadlinePassed()
    {
        if (_deadline is null || (!_deadline.IsCancellationRequested && Elapsed < Call.Timeout))
        {
            return;
        }

        _callerToken.ThrowIfCancellationRequested();
        throw new JevTimeoutException(Operation, Call.Timeout, Transmission, innerException: null);
    }

    public void Succeeded(int statusCode, string? requestId, int requestBytes, int responseBytes, JevUsage? usage)
    {
        _outcome = JevTelemetry.Outcomes.Success;

        if (_activity is { IsAllDataRequested: true } activity)
        {
            activity.SetTag(JevTelemetry.OutcomeTag, _outcome);
            activity.SetTag(JevTelemetry.AttemptsTag, Attempts);
            activity.SetTag(JevTelemetry.StatusCodeTag, statusCode);
            activity.SetTag(JevTelemetry.RequestBytesTag, requestBytes);
            activity.SetTag(JevTelemetry.ResponseBytesTag, responseBytes);

            if (requestId is not null)
            {
                activity.SetTag(JevTelemetry.RequestIdTag, requestId);
            }

            if (usage is { } u)
            {
                activity.SetTag(JevTelemetry.InputTokensTag, u.InputTokens);
                activity.SetTag(JevTelemetry.OutputTokensTag, u.OutputTokens);
            }
        }

        if (usage is { } tokens && JevTelemetry.Tokens.Enabled)
        {
            JevTelemetry.Tokens.Add(tokens.InputTokens, new KeyValuePair<string, object?>(JevTelemetry.TokenTypeTag, "input"));
            JevTelemetry.Tokens.Add(tokens.OutputTokens, new KeyValuePair<string, object?>(JevTelemetry.TokenTypeTag, "output"));
        }
    }

    /// <summary>
    /// Turns a failure into the exception the caller should see, and records it.
    /// </summary>
    /// <returns>The exception to throw: <paramref name="exception"/> itself when it needs no translation.</returns>
    public Exception Classify(Exception exception)
    {
        var translated = exception switch
        {
            // The caller asked for this, whatever else happened at the same moment.
            OperationCanceledException when _callerToken.IsCancellationRequested
                => new OperationCanceledException("The call was canceled.", exception, _callerToken),

            // A cancellation that surfaced because the deadline fired is the deadline. Only a
            // cancellation: an HTTP error that arrived just before the deadline is still that error.
            OperationCanceledException when _deadline?.IsCancellationRequested == true
                => new JevTimeoutException(Operation, Call.Timeout, Transmission, innerException: null),

            // HttpClient.Timeout: HttpClient cancels with a TimeoutException inside.
            OperationCanceledException { InnerException: TimeoutException }
                => new JevTransportException(Operation, JevTransportFailure.HttpClientTimeout, JevRequestTransmission.Unknown, exception),

            // A cancellation nobody here asked for, from a handler. Not reported as the caller's.
            OperationCanceledException
                => new JevTransportException(Operation, JevTransportFailure.Unknown, JevRequestTransmission.Unknown, exception),

            HttpRequestException request
                => TransportFailures.FromSend(Operation, request),

            _ => exception,
        };

        _outcome = JevTelemetry.OutcomeOf(translated);

        if (_activity is { IsAllDataRequested: true } activity)
        {
            activity.SetTag(JevTelemetry.OutcomeTag, _outcome);
            activity.SetTag(JevTelemetry.AttemptsTag, Attempts);
            activity.SetTag(JevTelemetry.ErrorTypeTag, translated.GetType().FullName);

            if (translated is JevHttpException http)
            {
                activity.SetTag(JevTelemetry.StatusCodeTag, http.StatusCode);

                if (http.RequestId is not null)
                {
                    activity.SetTag(JevTelemetry.RequestIdTag, http.RequestId);
                }
            }

            // The status description is the outcome, never the exception message.
            activity.SetStatus(ActivityStatusCode.Error, _outcome);
        }

        return translated;
    }

    public void Dispose()
    {
        if (_admitted)
        {
            _gate!.Exit();
            _admitted = false;
        }

        _linked?.Dispose();
        _deadline?.Dispose();

        var operationTag = new KeyValuePair<string, object?>(JevTelemetry.OperationTag, Operations.TagValue(Operation));

        if (JevTelemetry.Duration.Enabled)
        {
            JevTelemetry.Duration.Record(Elapsed.TotalSeconds, operationTag, new KeyValuePair<string, object?>(JevTelemetry.OutcomeTag, _outcome));
        }

        if (JevTelemetry.Active.Enabled)
        {
            JevTelemetry.Active.Add(-1, operationTag);
        }

        _activity?.Dispose();
    }
}

/// <summary>Classifies an <see cref="HttpRequestException"/> by how far the request got.</summary>
internal static class TransportFailures
{
    public static JevTransportException FromSend(JevOperation operation, HttpRequestException exception)
        => exception.HttpRequestError switch
        {
            // The connection was never established, so nothing was sent.
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError
                => new JevTransportException(operation, JevTransportFailure.Connect, JevRequestTransmission.NotStarted, exception),

            _ => new JevTransportException(operation, JevTransportFailure.Unknown, JevRequestTransmission.Unknown, exception),
        };
}
