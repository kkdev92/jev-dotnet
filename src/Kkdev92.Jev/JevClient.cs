using System.Net;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.Diagnostics;
using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Requests;
using Kkdev92.Jev.Responses;
using Kkdev92.Jev.Transport;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev;

/// <summary>
/// Asks Jev, TypeSafe's System One model, the questions of a <see cref="JevDecisionPlan"/> about a
/// state, and returns typed answers.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe and meant to be long-lived: create one per configuration and share it. It holds no
/// per-call state and no credential of its own — the key is asked for per attempt and placed on that
/// request only.
/// </para>
/// <para>
/// The <see cref="HttpClient"/> is borrowed, never owned: the client does not dispose it, does not
/// change its <see cref="HttpClient.DefaultRequestHeaders"/>, <see cref="HttpClient.BaseAddress"/> or
/// <see cref="HttpClient.Timeout"/>, and builds every request absolutely. Its lifetime, handler,
/// proxy and connection pooling stay the application's decisions. Two things are worth setting on
/// it: <see cref="HttpClient.Timeout"/> to <see cref="Timeout.InfiniteTimeSpan"/>, so that
/// <see cref="JevClientOptions.Timeout"/> is the one limit on a call, and redirects off, so that a
/// key is never replayed to another host.
/// </para>
/// <para>
/// Every failure is a <see cref="JevException"/>, a <see cref="JevTimeoutException"/>, an
/// <see cref="OperationCanceledException"/> for the caller's own cancellation, or an argument
/// exception thrown before anything starts. None of their messages carry the state, the questions,
/// the key or the service's own error text.
/// </para>
/// </remarks>
public sealed class JevClient
{
    private static readonly string UserAgent = "Kkdev92.Jev/" + JevTelemetry.Version;

    private readonly HttpClient _httpClient;
    private readonly ClientSettings _settings;
    private readonly AdmissionGate? _gate;
    private readonly Uri _systemOneUri;
    private readonly Uri _modelsUri;

    /// <summary>Creates a client over an <see cref="HttpClient"/> the caller owns.</summary>
    /// <param name="httpClient">The client to send with. Not disposed or reconfigured.</param>
    /// <param name="options">The configuration, validated and copied now.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The options are invalid. The message names the option.</exception>
    public JevClient(HttpClient httpClient, JevClientOptions options)
        : this(httpClient, options, sharedGate: null)
    {
    }

    /// <summary>Creates a client that shares an admission gate with others, as the DI package does for the clients a container creates.</summary>
    internal JevClient(HttpClient httpClient, JevClientOptions options, AdmissionGate? sharedGate)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _settings = ClientSettings.From(options);
        _httpClient = httpClient;
        _gate = sharedGate ?? (_settings.ConcurrencyLimit is { } limit ? new AdmissionGate(limit, _settings.MaxQueuedRequests) : null);
        _systemOneUri = new Uri(_settings.BaseAddress, JevContract.SystemOnePath);
        _modelsUri = new Uri(_settings.BaseAddress, JevContract.ModelsPath);
    }

    /// <summary>Evaluates text against a plan.</summary>
    /// <param name="state">The text to evaluate, sent as a JSON string. A string that looks like JSON is still text.</param>
    /// <param name="plan">The questions to ask.</param>
    /// <param name="options">Overrides for this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>An answer to every question of the plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>. Thrown before the call starts.</exception>
    /// <exception cref="ArgumentException">The state or an option is invalid. Thrown before the call starts.</exception>
    /// <exception cref="JevException">Returned through the task: the call failed. See the derived types.</exception>
    /// <exception cref="JevTimeoutException">Returned through the task: the call's deadline passed.</exception>
    /// <exception cref="OperationCanceledException">Returned through the task: <paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<JevResult> EvaluateAsync(string state, JevDecisionPlan plan, JevRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        TextRules.ThrowIfMalformed(state, nameof(state), "state");

        var call = CallSettings.Resolve(_settings, options);
        return EvaluateCoreAsync(StateSource.FromContent(JevContent.FromText(state)), plan, call, cancellationToken);
    }

    /// <summary>Evaluates a typed state, serialized with metadata you supply, against a plan.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="state">The state. Serialized once per call, before the first attempt; a retry sends the same bytes.</param>
    /// <param name="stateTypeInfo">
    /// Source-generated metadata for <typeparamref name="TState"/>, from a <c>JsonSerializerContext</c>
    /// of your own. Required rather than inferred: inferring it takes reflection, which Native AOT
    /// does not have. It must serialize the state to a JSON object, array or string.
    /// </param>
    /// <param name="plan">The questions to ask.</param>
    /// <param name="options">Overrides for this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>An answer to every question of the plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>. Thrown before the call starts.</exception>
    /// <exception cref="ArgumentException">An option is invalid (thrown before the call starts), or the state serialized to a number, boolean or null (returned through the task).</exception>
    /// <exception cref="JevException">Returned through the task: the call failed.</exception>
    /// <exception cref="JevTimeoutException">Returned through the task: the call's deadline passed.</exception>
    /// <exception cref="OperationCanceledException">Returned through the task: <paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<JevResult> EvaluateAsync<TState>(TState state, JsonTypeInfo<TState> stateTypeInfo, JevDecisionPlan plan, JevRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state), "The state must not be null: the API does not accept a null state.");
        }

        ArgumentNullException.ThrowIfNull(stateTypeInfo);
        ArgumentNullException.ThrowIfNull(plan);

        var call = CallSettings.Resolve(_settings, options);
        return EvaluateCoreAsync(StateSource.FromTyped(state, stateTypeInfo), plan, call, cancellationToken);
    }

    /// <summary>Evaluates prepared content — text, or a JSON object or array — against a plan.</summary>
    /// <param name="state">
    /// The state. Content created once with <see cref="JevContent.FromJson(ReadOnlySpan{byte})"/> is
    /// validated once and sent as it is by every call that uses it.
    /// </param>
    /// <param name="plan">The questions to ask.</param>
    /// <param name="options">Overrides for this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>An answer to every question of the plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>. Thrown before the call starts.</exception>
    /// <exception cref="ArgumentException">The state is unspecified or null, or an option is invalid. Thrown before the call starts.</exception>
    /// <exception cref="JevException">Returned through the task: the call failed.</exception>
    /// <exception cref="JevTimeoutException">Returned through the task: the call's deadline passed.</exception>
    /// <exception cref="OperationCanceledException">Returned through the task: <paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<JevResult> EvaluateContentAsync(JevContent state, JevDecisionPlan plan, JevRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (state.Kind is JevContentKind.Unspecified or JevContentKind.Null)
        {
            throw new ArgumentException("The state must be text, an object or an array. The API does not accept a missing or null state.", nameof(state));
        }

        if (!state.IsWellFormed)
        {
            throw new ArgumentException("The state contains a lone surrogate, which has no UTF-8 encoding and would be replaced in transit.", nameof(state));
        }

        var call = CallSettings.Resolve(_settings, options);
        return EvaluateCoreAsync(StateSource.FromContent(state), plan, call, cancellationToken);
    }

    /// <summary>Lists the models and aliases the key can use.</summary>
    /// <param name="options">Overrides for this call. <see cref="JevRequestOptions.Model"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The models, as the service listed them. Not cached: each call asks again.</returns>
    /// <remarks>
    /// Nothing calls this implicitly, and the list is not needed to evaluate: it currently holds
    /// aliases only, and a versioned model id is accepted whether or not it appears in it.
    /// </remarks>
    /// <exception cref="ArgumentException">An option is invalid. Thrown before the call starts.</exception>
    /// <exception cref="JevException">Returned through the task: the call failed.</exception>
    /// <exception cref="JevTimeoutException">Returned through the task: the call's deadline passed.</exception>
    /// <exception cref="OperationCanceledException">Returned through the task: <paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IReadOnlyList<JevModel>> GetModelsAsync(JevRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = CallSettings.Resolve(_settings, options);
        return GetModelsCoreAsync(call, cancellationToken);
    }

    private async Task<JevResult> EvaluateCoreAsync(StateSource state, JevDecisionPlan plan, CallSettings call, CancellationToken cancellationToken)
    {
        using var scope = new CallScope(_settings, _gate, JevOperation.Evaluate, call, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await scope.AdmitAsync().ConfigureAwait(false);

            // Encoded once, after admission, so a queued call holds no body; sent unchanged by every attempt.
            var body = RequestBodyWriter.WriteEvaluate(call.Model, state, plan, _settings.MaxRequestBodyBytes);
            scope.Token.ThrowIfCancellationRequested();

            var exchange = await ExchangeAsync(scope, HttpMethod.Post, _systemOneUri, body).ConfigureAwait(false);
            var decoded = Decode(exchange, plan);
            var warnings = ConsistencyCheck.Run(plan, decoded, _settings.AbsoluteTolerance, _settings.RelativeTolerance, _settings.NumericalConsistency, exchange.RequestId);

            scope.ThrowIfDeadlinePassed();

            // Not a conditional expression: ReadOnlyMemory converts implicitly from an array, so a
            // null in either arm becomes an empty memory — a value — and the result would claim a
            // capture that never happened.
            ReadOnlyMemory<byte>? raw = null;

            if (call.CaptureRawResponse)
            {
                raw = exchange.Body.ToArray();
            }
            var metadata = new JevResultMetadata(exchange.RequestId, scope.Attempts, exchange.HttpVersion, scope.Elapsed, warnings);
            var result = new JevResult(plan, call.Model, decoded, metadata, raw);

            scope.Succeeded(exchange.StatusCode, exchange.RequestId, body.Length, exchange.Body.Count, decoded.Usage);
            return result;
        }
        catch (Exception ex)
        {
            var translated = scope.Classify(ex);

            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }
    }

    private async Task<IReadOnlyList<JevModel>> GetModelsCoreAsync(CallSettings call, CancellationToken cancellationToken)
    {
        using var scope = new CallScope(_settings, _gate, JevOperation.ListModels, call, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await scope.AdmitAsync().ConfigureAwait(false);

            var exchange = await ExchangeAsync(scope, HttpMethod.Get, _modelsUri, default).ConfigureAwait(false);

            if (!Utf8.IsValid(exchange.Body))
            {
                throw new JevProtocolException(JevOperation.ListModels, JevProtocolError.InvalidUtf8, exchange.RequestId);
            }

            var models = ModelListDecoder.Decode(exchange.Body, exchange.RequestId);
            scope.ThrowIfDeadlinePassed();
            scope.Succeeded(exchange.StatusCode, exchange.RequestId, 0, exchange.Body.Count, usage: null);
            return models;
        }
        catch (Exception ex)
        {
            var translated = scope.Classify(ex);

            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }
    }

    /// <summary>Checks the body is UTF-8 and decodes it, attaching the request id to any refusal.</summary>
    private DecodedResponse Decode(Exchange exchange, JevDecisionPlan plan)
    {
        try
        {
            // The JSON reader does not validate the bytes inside strings it skips, and does not
            // validate them at all until a string is decoded. The whole body is checked here, once.
            if (!Utf8.IsValid(exchange.Body))
            {
                throw new JevProtocolException(JevOperation.Evaluate, JevProtocolError.InvalidUtf8);
            }

            return SystemOneResponseReader.Read(exchange.Body, plan, _settings.AbsoluteTolerance);
        }
        catch (JevProtocolException ex) when (exchange.RequestId is not null && ex.RequestId is null)
        {
            throw new JevProtocolException(ex.Operation, ex.Error, exchange.RequestId);
        }
    }

    /// <summary>
    /// Sends the request, retrying where the rules allow, until a successful body has been read in
    /// full or a failure has been decided.
    /// </summary>
    /// <remarks>
    /// Each attempt builds a new <see cref="HttpRequestMessage"/> over the same body bytes and asks
    /// the credential for the key again. The response is disposed before any backoff, so a waiting
    /// retry holds no connection. What returns holds no reference to a response or a stream.
    /// </remarks>
    private async Task<Exchange> ExchangeAsync(CallScope scope, HttpMethod method, Uri uri, ArraySegment<byte> body)
    {
        while (true)
        {
            scope.Attempts++;
            scope.Transmission = JevRequestTransmission.NotStarted;

            var key = await scope.Call.Credential.GetApiKeyAsync(scope.Token).ConfigureAwait(false);

            if (!TextRules.IsUsableApiKey(key))
            {
                // Refused before anything is sent. The key is not in the message.
                throw new InvalidOperationException("The credential returned an API key that is empty or contains whitespace, control or non-ASCII characters.");
            }

            TimeSpan? retryDelay;

            using (var request = CreateRequest(method, uri, body, key))
            {
                HttpResponseMessage response;
                scope.Transmission = JevRequestTransmission.Unknown;

                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (!scope.Token.IsCancellationRequested)
                {
                    var failure = TransportFailures.FromSend(scope.Operation, ex);
                    scope.Transmission = failure.Transmission;
                    retryDelay = scope.DecideRetry(networkFailure: true, serverWait: null);

                    if (retryDelay is null)
                    {
                        throw failure;
                    }

                    await scope.DelayAsync(retryDelay.Value).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    scope.Transmission = JevRequestTransmission.Started;
                    var status = (int)response.StatusCode;
                    var requestId = ResponseHeaders.RequestId(response);

                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var content = await BoundedContent.ReadAsync(response.Content, _settings.MaxResponseBodyBytes, scope.Operation, scope.Token).ConfigureAwait(false);
                            return new Exchange(content, requestId, response.Version, status);
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException && !scope.Token.IsCancellationRequested)
                        {
                            // The service answered and the answer did not arrive in full. It may
                            // well have been billed; retrying it is a network-failure retry.
                            var failure = new JevTransportException(scope.Operation, JevTransportFailure.ResponseBody, JevRequestTransmission.Started, ex);
                            retryDelay = scope.DecideRetry(networkFailure: true, serverWait: null);

                            if (retryDelay is null)
                            {
                                throw failure;
                            }
                        }
                    }
                    else
                    {
                        var error = await ReadErrorAsync(scope, response, requestId).ConfigureAwait(false);
                        retryDelay = RetryRules.IsRetryableStatus(status) ? scope.DecideRetry(networkFailure: false, error.RetryAfter) : null;

                        if (retryDelay is null)
                        {
                            throw error;
                        }
                    }
                }
            }

            // Both the request and the response are disposed before waiting.
            await scope.DelayAsync(retryDelay.Value).ConfigureAwait(false);
        }
    }

    private async Task<JevHttpException> ReadErrorAsync(CallScope scope, HttpResponseMessage response, string? requestId)
    {
        ArraySegment<byte> body = default;

        try
        {
            body = await BoundedContent.ReadTruncatedAsync(response.Content, _settings.MaxErrorBodyBytes, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !scope.Token.IsCancellationRequested)
        {
            // The status is the answer; a body that broke off adds nothing to it.
        }

        var (errorType, validationErrors) = ErrorBody.Parse(body);
        var retryAfter = ResponseHeaders.RetryAfter(response.Headers, _settings.TimeProvider);
        ReadOnlyMemory<byte>? captured = null;

        if (scope.Call.CaptureErrorBody && body.Count > 0)
        {
            captured = body.ToArray();
        }

        return new JevHttpException(scope.Operation, (int)response.StatusCode, requestId, errorType, retryAfter, validationErrors, captured);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, ArraySegment<byte> body, string key)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            // HTTP/2 where the server offers it — api.typesafe.ai negotiates h2 — and HTTP/1.1 where
            // it does not. Never exact: a proxy that cannot do h2 must not fail the call.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (body.Array is not null)
        {
            var content = new ByteArrayContent(body.Array, body.Offset, body.Count);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Content = content;
        }

        // Added without validation because every value is fixed or has been checked: the key is
        // printable ASCII with no whitespace, so it cannot end the header line.
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        return request;
    }

    /// <summary>A successful response, read in full and detached from the connection.</summary>
    private readonly record struct Exchange(ArraySegment<byte> Body, string? RequestId, Version HttpVersion, int StatusCode);
}
