namespace Kkdev92.Jev;

/// <summary>
/// The names under which the SDK publishes traces and metrics, for subscribing to them.
/// </summary>
/// <remarks>
/// <para>
/// The SDK writes no logs and takes no logging dependency. It emits one
/// <see cref="System.Diagnostics.Activity"/> per call on <see cref="ActivitySourceName"/> and a few
/// instruments on <see cref="MeterName"/>, and does nothing with either unless a listener is
/// attached.
/// </para>
/// <para>
/// Tags are a fixed, short list of values the SDK chose: the operation, the outcome, the status,
/// sizes, attempts, token counts and the request id. Never a request or response body, a key, a
/// question id, a label, a model name, or a URL. <see cref="System.Net.Http.HttpClient"/>'s own
/// instrumentation records the request URL, which for this API carries nothing but the path.
/// </para>
/// </remarks>
public static class JevDiagnostics
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name: <c>Kkdev92.Jev</c>.</summary>
    public const string ActivitySourceName = "Kkdev92.Jev";

    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> name: <c>Kkdev92.Jev</c>.</summary>
    public const string MeterName = "Kkdev92.Jev";
}
