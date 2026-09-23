using System.Globalization;

namespace Kkdev92.Jev;

/// <summary>
/// The call did not complete within <see cref="JevClientOptions.Timeout"/> (or the per-call
/// <see cref="JevRequestOptions.Timeout"/>).
/// </summary>
/// <remarks>
/// <para>
/// The deadline covers the whole call — waiting for admission, asking the credential for a key,
/// every attempt, every backoff, reading the body and decoding it — and is never reset by a retry.
/// A result is never returned after it has passed.
/// </para>
/// <para>
/// Derives from <see cref="TimeoutException"/> rather than <see cref="JevException"/>, so existing
/// handling of timeouts applies. <see cref="Transmission"/> is usually
/// <see cref="JevRequestTransmission.Unknown"/>: the request may have been processed.
/// </para>
/// </remarks>
public sealed class JevTimeoutException : TimeoutException
{
    internal JevTimeoutException(JevOperation operation, TimeSpan timeout, JevRequestTransmission transmission, Exception? innerException)
        : base($"{Operations.Describe(operation)} did not complete within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s (JevClientOptions.Timeout).", innerException)
    {
        Operation = operation;
        Timeout = timeout;
        Transmission = transmission;
    }

    /// <summary>The operation that timed out.</summary>
    public JevOperation Operation { get; }

    /// <summary>The deadline that passed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>How far the request got before the deadline passed.</summary>
    public JevRequestTransmission Transmission { get; }
}
