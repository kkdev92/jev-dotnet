namespace Kkdev92.Jev;

/// <summary>
/// Configuration for a <see cref="JevClient"/>, validated and copied when the client is created.
/// </summary>
/// <remarks>
/// <para>
/// Changing an instance after a client has been created from it does not change that client.
/// Every default here is a choice this SDK made, not a limit TypeSafe publishes; where the reason
/// is not obvious, the property gives it.
/// </para>
/// <para>
/// Per-call overrides live on <see cref="JevRequestOptions"/>. Precedence is: the per-call value,
/// then this, then the SDK default.
/// </para>
/// </remarks>
public sealed class JevClientOptions
{
    /// <summary>The TypeSafe API origin, <c>https://api.typesafe.ai</c>.</summary>
    public static Uri DefaultBaseAddress { get; } = new(Wire.JevContract.DefaultBaseAddress, UriKind.Absolute);

    /// <summary><c>jev-latest</c>, the alias TypeSafe's documentation uses.</summary>
    public const string DefaultModelName = Wire.JevContract.DefaultModel;

    /// <summary>
    /// Where the API key comes from. Required: the client never looks for a key on its own.
    /// </summary>
    public IJevCredential? Credential { get; set; }

    /// <summary>
    /// The model to use when a call does not name one. <c>jev-latest</c> by default.
    /// </summary>
    /// <remarks>
    /// <c>jev-latest</c> is an alias that moves when TypeSafe ships a release, so answers behind it
    /// can change without a change on your side. When results must stay comparable — confidence
    /// thresholds tuned on one version, an evaluation set — name a versioned model such as
    /// <c>jev-1.13.0</c> instead. <see cref="JevResult.ActualModel"/> reports the version that
    /// answered either way.
    /// </remarks>
    public string DefaultModel { get; set; } = DefaultModelName;

    /// <summary>
    /// The API origin. An absolute HTTPS URI with no path, query, fragment or user information.
    /// </summary>
    public Uri BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>
    /// The deadline for a whole call, 30 seconds by default. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    /// <remarks>
    /// One budget for everything: waiting for admission, getting the key, every attempt and every
    /// backoff, reading the body and decoding it. A retry does not reset it, so a call that retries
    /// is still bounded.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times to retry after the first attempt fails with a retryable status. 0 by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is retried unless asked, because a second call is a second evaluation: it can be
    /// billed again, and its answer need not match the first. Whether that is acceptable is the
    /// application's decision.
    /// </para>
    /// <para>
    /// When set, <c>408</c>, <c>429</c>, <c>502</c>, <c>503</c>, <c>504</c> and <c>529</c> are
    /// retried, within <see cref="Timeout"/>. A network failure is retried only if
    /// <see cref="RetryNetworkFailures"/> is also set. Nothing else is: not 401, 403 or 422, not a
    /// response the SDK cannot accept, not a cancellation.
    /// </para>
    /// </remarks>
    public int AdditionalRetries { get; set; }

    /// <summary>The first backoff delay, 250 ms by default. Doubled per attempt up to <see cref="RetryBackoffMaximum"/>, with full jitter.</summary>
    public TimeSpan RetryBackoffInitial { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest computed backoff, 5 seconds by default. A server's own <c>Retry-After</c> is not capped by this.</summary>
    public TimeSpan RetryBackoffMaximum { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest a server-requested wait may be, 30 seconds by default. A longer one ends the call
    /// with the error that carried it, rather than being cut short.
    /// </summary>
    /// <remarks>
    /// Retrying sooner than the server asked arrives at a service that has just said it is not
    /// ready. So a long <c>Retry-After</c> is honoured or not acted on at all, never shortened; the
    /// resulting <see cref="JevHttpException.RetryAfter"/> lets the caller schedule the retry.
    /// </remarks>
    public TimeSpan MaximumServerRetryWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a connection failure or an interrupted response is retried. False by default, and
    /// has no effect unless <see cref="AdditionalRetries"/> is above zero.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AdditionalRetries"/> because the outcome of the failed attempt is
    /// unknown: the service may already have evaluated, and billed, the request it never finished
    /// answering.
    /// </remarks>
    public bool RetryNetworkFailures { get; set; }

    /// <summary>The largest request body the client will send, 4 MiB by default. Not the model's context length, which is measured in tokens.</summary>
    public int MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The largest successful response body the client will read, 4 MiB by default.</summary>
    public int MaxResponseBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>How much of an error body the client reads, 64 KiB by default. Past it the body is truncated, not refused.</summary>
    public int MaxErrorBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// The most calls in flight at once through this client. Unlimited by default.
    /// </summary>
    /// <remarks>
    /// A call holds its permit for its whole duration, backoff included. This bounds one client; it
    /// cannot know about other clients, other processes, or the account-wide rate limit.
    /// </remarks>
    public int? ConcurrencyLimit { get; set; }

    /// <summary>
    /// How many calls may wait for a permit when <see cref="ConcurrencyLimit"/> is reached. 0 by
    /// default: a call that finds no permit fails at once with <see cref="JevLimitException"/>.
    /// Waiting counts against <see cref="Timeout"/>.
    /// </summary>
    public int MaxQueuedRequests { get; set; }

    /// <summary>Whether a copy of each successful response body is kept on <see cref="JevResult.RawResponseBody"/>. False by default.</summary>
    public bool CaptureRawResponse { get; set; }

    /// <summary>Whether a copy of each error body, up to <see cref="MaxErrorBodyBytes"/>, is kept on <see cref="JevHttpException.ErrorBody"/>. False by default.</summary>
    public bool CaptureErrorBody { get; set; }

    /// <summary>What a response whose numbers disagree with each other produces. <see cref="JevNumericalConsistency.Report"/> by default.</summary>
    public JevNumericalConsistency NumericalConsistency { get; set; } = JevNumericalConsistency.Report;

    /// <summary>
    /// The absolute tolerance of the consistency checks and of the documented ranges, 1e-6 by default.
    /// </summary>
    /// <remarks>
    /// A provisional value of this SDK's, not a precision TypeSafe states. Its OpenAPI document says
    /// the probabilities sum to "approximately 1", and its HTTP reference that they sum to 1.
    /// </remarks>
    public double NumericalAbsoluteTolerance { get; set; } = 1e-6;

    /// <summary>The relative tolerance of the consistency checks, 1e-6 by default. Provisional, like <see cref="NumericalAbsoluteTolerance"/>.</summary>
    public double NumericalRelativeTolerance { get; set; } = 1e-6;

    /// <summary>The clock for the deadline and for backoff. <see cref="System.TimeProvider.System"/> by default; replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>The source of backoff jitter, a draw from [0, 1). Replaced by tests that need a fixed schedule.</summary>
    internal Func<double>? JitterSource { get; set; }
}

/// <summary>What happens when an answer's numbers disagree with each other.</summary>
/// <remarks>
/// The checks are that a distribution sums to one, that a choice is the most probable option, and
/// that a score is the expectation of its levels, each within the configured tolerance. They never
/// change a value: nothing is renormalised or recomputed, and confidence is never derived. A value
/// outside its documented range — a probability of 1.5 — is refused in either mode.
/// </remarks>
public enum JevNumericalConsistency
{
    /// <summary>Record a <see cref="JevConsistencyWarning"/> on <see cref="JevResultMetadata.Warnings"/> and return the answer.</summary>
    Report = 0,

    /// <summary>Refuse the response with a <see cref="JevProtocolException"/>.</summary>
    Strict = 1,
}
