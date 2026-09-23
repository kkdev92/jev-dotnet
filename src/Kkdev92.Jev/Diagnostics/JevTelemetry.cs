using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Kkdev92.Jev.Diagnostics;

/// <summary>
/// The SDK's one <see cref="ActivitySource"/> and one <see cref="Meter"/>, and the fixed tags it puts on them.
/// </summary>
/// <remarks>
/// <para>
/// Created once per process and never disposed, as the guidance for library instrumentation
/// recommends. No telemetry schema URL is declared: this SDK publishes no schema, and a URL naming
/// one that does not exist would be worse than none.
/// </para>
/// <para>
/// Cheap when nobody listens. An activity that no listener samples is never created, tags are
/// computed only when <see cref="Activity.IsAllDataRequested"/> is set, and an instrument with no
/// listener is skipped by its <see cref="Instrument.Enabled"/> check.
/// </para>
/// </remarks>
internal static class JevTelemetry
{
    public const string OperationTag = "jev.operation";
    public const string OutcomeTag = "jev.outcome";
    public const string AttemptsTag = "jev.attempts";
    public const string RequestIdTag = "jev.response.request_id";
    public const string RequestBytesTag = "jev.request.body.size";
    public const string ResponseBytesTag = "jev.response.body.size";
    public const string InputTokensTag = "jev.usage.input_tokens";
    public const string OutputTokensTag = "jev.usage.output_tokens";
    public const string TokenTypeTag = "jev.token.type";
    public const string StatusCodeTag = "http.response.status_code";
    public const string ErrorTypeTag = "error.type";

    /// <summary>Every tag the SDK can emit. Asserted by a test, so adding one is a reviewed decision.</summary>
    public static readonly IReadOnlyList<string> AllTags =
    [
        OperationTag, OutcomeTag, AttemptsTag, RequestIdTag, RequestBytesTag, ResponseBytesTag,
        InputTokensTag, OutputTokensTag, TokenTypeTag, StatusCodeTag, ErrorTypeTag,
    ];

    public static readonly string Version = typeof(JevTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static readonly ActivitySource Source = new(JevDiagnostics.ActivitySourceName, Version);

    private static readonly Meter Meter = new(JevDiagnostics.MeterName, Version);

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "jev.client.operation.duration",
        unit: "s",
        description: "Duration of a whole call, from start to decoded result or failure, including admission and backoff.");

    public static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>(
        "jev.client.operation.active",
        unit: "{operation}",
        description: "Calls in progress.");

    public static readonly Histogram<double> QueueDuration = Meter.CreateHistogram<double>(
        "jev.client.queue.duration",
        unit: "s",
        description: "Time spent waiting for a concurrency permit.");

    public static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "jev.client.retries",
        unit: "{retry}",
        description: "Attempts after the first.");

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "jev.client.token.usage",
        unit: "{token}",
        description: "Tokens the service reported, by jev.token.type.");

    /// <summary>The bounded set of outcomes, the only values <see cref="OutcomeTag"/> takes.</summary>
    public static class Outcomes
    {
        public const string Success = "success";
        public const string HttpError = "http_error";
        public const string ProtocolError = "protocol_error";
        public const string TransportError = "transport_error";
        public const string Timeout = "timeout";
        public const string Canceled = "canceled";
        public const string LimitExceeded = "limit_exceeded";
        public const string Error = "error";
    }

    public static Activity? Start(JevOperation operation)
    {
        var activity = Source.StartActivity(operation == JevOperation.Evaluate ? "Jev.Evaluate" : "Jev.ListModels", ActivityKind.Internal);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(OperationTag, Operations.TagValue(operation));
        }

        return activity;
    }

    public static string OutcomeOf(Exception exception) => exception switch
    {
        JevHttpException => Outcomes.HttpError,
        JevProtocolException => Outcomes.ProtocolError,
        JevTransportException => Outcomes.TransportError,
        JevTimeoutException => Outcomes.Timeout,
        OperationCanceledException => Outcomes.Canceled,
        JevLimitException => Outcomes.LimitExceeded,
        _ => Outcomes.Error,
    };
}
