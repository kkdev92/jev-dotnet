using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Transport;

/// <summary>Which failures may be retried, and how long to wait before doing so.</summary>
internal static class RetryRules
{
    /// <summary>
    /// The statuses retried when retrying is enabled: timeouts, rate limiting, overload and gateway
    /// failures. Not 500, which says the request itself broke something; not 401, 403 or 422, which
    /// will fail the same way again.
    /// </summary>
    public static bool IsRetryableStatus(int status)
        => status is 408 or 429 or 502 or 503 or 504 || status == JevContract.OverloadedStatus;

    /// <summary>
    /// Full-jitter exponential backoff: a uniform draw over <c>[0, min(maximum, initial × 2^retry))</c>.
    /// </summary>
    /// <remarks>
    /// Computed in <see cref="double"/> ticks and capped before conversion, so a large retry index
    /// saturates at the maximum rather than overflowing.
    /// </remarks>
    /// <param name="retry">Zero for the first retry.</param>
    /// <param name="initial">The first ceiling.</param>
    /// <param name="maximum">The largest ceiling.</param>
    /// <param name="random">A draw from [0, 1).</param>
    public static TimeSpan Backoff(int retry, TimeSpan initial, TimeSpan maximum, double random)
    {
        var ceiling = Math.Min(maximum.Ticks, initial.Ticks * Math.Pow(2, retry));
        return TimeSpan.FromTicks((long)(Math.Clamp(random, 0, 1) * ceiling));
    }
}
