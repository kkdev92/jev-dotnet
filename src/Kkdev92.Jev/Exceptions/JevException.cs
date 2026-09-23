namespace Kkdev92.Jev;

/// <summary>
/// Base class for failures the SDK reports about a call: an HTTP error, a transport failure, a
/// response it cannot accept, or a local limit.
/// </summary>
/// <remarks>
/// <para>
/// A timeout of the SDK's own deadline is reported as <see cref="JevTimeoutException"/>, which
/// derives from <see cref="TimeoutException"/> instead, so that code already catching timeouts
/// keeps doing so. Cancellation by the caller is an <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Every message is built from values the SDK chose — the operation, a status code, a fixed
/// description — and never from the request, the response body, the API key or a question id.
/// A message is the string most likely to end up in a log.
/// </para>
/// </remarks>
public abstract class JevException : Exception
{
    private protected JevException(string message, JevOperation operation, JevRequestTransmission transmission, Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        Transmission = transmission;
    }

    /// <summary>The operation that failed.</summary>
    public JevOperation Operation { get; }

    /// <summary>
    /// How far the request got. Tells a caller deciding whether to try again whether the service
    /// may already have done the work — and billed for it.
    /// </summary>
    public JevRequestTransmission Transmission { get; }
}

/// <summary>An operation of the TypeSafe API.</summary>
public enum JevOperation
{
    /// <summary><c>POST /v1/systemone</c>: evaluating a state against a plan.</summary>
    Evaluate = 1,

    /// <summary><c>GET /v1/models</c>: listing the models the key can use.</summary>
    ListModels = 2,
}

/// <summary>How far a failed request got.</summary>
/// <remarks>
/// Deliberately coarse. The SDK can tell that nothing was sent, or that the service answered; it
/// cannot tell whether a request that timed out mid-flight was processed, and does not guess.
/// </remarks>
public enum JevRequestTransmission
{
    /// <summary>Nothing was sent: the failure happened before the request left the process.</summary>
    NotStarted = 0,

    /// <summary>The service received the request and answered it.</summary>
    Started = 1,

    /// <summary>The request may or may not have reached the service, and may or may not have been processed.</summary>
    Unknown = 2,
}
