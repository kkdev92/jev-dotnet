using System.Globalization;
using System.Net.Http.Headers;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Responses;

/// <summary>The two response headers the SDK reads: the request id, and how long to wait before retrying.</summary>
internal static class ResponseHeaders
{
    /// <summary>The longest request id kept. The observed ones are 36 characters.</summary>
    private const int MaxRequestIdLength = 128;

    /// <summary>
    /// The <c>x-typesafe-request-id</c> header, when it looks like an identifier.
    /// </summary>
    /// <remarks>
    /// The value ends up in exception messages and on traces, so it is held to a shape — at most 128
    /// characters of letters, digits, '-' and '_' — rather than trusted. A proxy or a misbehaving
    /// server could otherwise put anything there. Anything else is dropped, not truncated.
    /// </remarks>
    public static string? RequestId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(JevContract.RequestIdHeader, out var values))
        {
            return null;
        }

        string? found = null;

        foreach (var value in values)
        {
            if (found is not null)
            {
                // Two request ids is not one request id.
                return null;
            }

            found = value;
        }

        return found is { Length: > 0 and <= MaxRequestIdLength } && found.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? found
            : null;
    }

    /// <summary>
    /// How long the server asked the client to wait: <c>retry-after-ms</c> when present and valid,
    /// otherwise <c>Retry-After</c> as delta-seconds or an HTTP-date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The millisecond header wins when both are present: it is the more precise of the two. A date
    /// in the past means now. A value that cannot be read at all is treated as absent, so the caller
    /// falls back to its own backoff rather than to zero.
    /// </para>
    /// <para>
    /// A number is read however large it is. A wait read as absent would be replaced by the
    /// caller's backoff, which is seconds, so a long one has to stay long for the caller to decline
    /// it: one longer than a <see cref="TimeSpan"/> holds reads as <see cref="TimeSpan.MaxValue"/>.
    /// </para>
    /// <para>
    /// The current time for an HTTP-date comes from the injected <see cref="TimeProvider"/>, which is
    /// the one place wall-clock time enters the SDK's arithmetic.
    /// </para>
    /// </remarks>
    public static TimeSpan? RetryAfter(HttpResponseHeaders headers, TimeProvider timeProvider)
    {
        if (headers.TryGetValues(JevContract.RetryAfterMillisecondsHeader, out var milliseconds)
            && Single(milliseconds) is { } raw
            && TryReadAmount(raw, out var ms))
        {
            return FromMilliseconds(ms);
        }

        var retryAfter = headers.RetryAfter;

        if (retryAfter?.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        // The header parser holds delta-seconds in an int and refuses a fraction. Either is still a
        // number of seconds the server asked for.
        if (headers.NonValidated.TryGetValues(RetryAfterHeader, out var unparsed)
            && Single(unparsed) is { } seconds
            && TryReadAmount(seconds, out var s))
        {
            return FromMilliseconds(s * 1000);
        }

        return null;
    }

    private const string RetryAfterHeader = "Retry-After";

    /// <summary>A non-negative decimal number. Infinity, from more digits than a double holds, is a number too.</summary>
    private static bool TryReadAmount(string raw, out double amount)
        => double.TryParse(raw.AsSpan().Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && !double.IsNaN(amount)
            && amount >= 0;

    private static TimeSpan FromMilliseconds(double milliseconds)
        => milliseconds * TimeSpan.TicksPerMillisecond >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);

    private static string? Single(IEnumerable<string> values)
    {
        string? found = null;

        foreach (var value in values)
        {
            if (found is not null)
            {
                return null;
            }

            found = value;
        }

        return found;
    }
}
