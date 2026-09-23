using System.Net.Http;

namespace Kkdev92.Jev;

/// <summary>
/// The request or its response did not make it: a connection that could not be made or was lost,
/// a response body cut short, or <see cref="HttpClient.Timeout"/> expiring.
/// </summary>
/// <remarks>
/// <see cref="JevException.Transmission"/> says whether anything was sent. When it is
/// <see cref="JevRequestTransmission.Unknown"/> the service may have processed and billed the
/// request, and a retry can do so again; that is why the SDK retries these only when
/// <see cref="JevClientOptions.RetryNetworkFailures"/> says to.
/// </remarks>
public sealed class JevTransportException : JevException
{
    internal JevTransportException(JevOperation operation, JevTransportFailure failure, JevRequestTransmission transmission, Exception? innerException)
        : base(Describe(operation, failure), operation, transmission, innerException)
    {
        Failure = failure;
    }

    /// <summary>What went wrong, as far as the SDK could tell.</summary>
    public JevTransportFailure Failure { get; }

    private static string Describe(JevOperation operation, JevTransportFailure failure) => failure switch
    {
        JevTransportFailure.Connect => $"Could not connect to the TypeSafe API for {Operations.Describe(operation)}. Nothing was sent.",
        JevTransportFailure.ResponseBody => $"The response to {Operations.Describe(operation)} ended before it was complete.",
        JevTransportFailure.HttpClientTimeout => $"HttpClient.Timeout expired during {Operations.Describe(operation)}. The SDK's own deadline is JevClientOptions.Timeout; consider setting HttpClient.Timeout to Timeout.InfiniteTimeSpan so that one limit governs the call.",
        _ => $"The request for {Operations.Describe(operation)} failed in transit.",
    };
}

/// <summary>The kind of transport failure.</summary>
public enum JevTransportFailure
{
    /// <summary>The request failed in transit and the SDK cannot say more.</summary>
    Unknown = 0,

    /// <summary>A connection could not be established: name resolution, TCP or TLS. Nothing was sent.</summary>
    Connect = 1,

    /// <summary>Headers arrived but the body did not arrive in full.</summary>
    ResponseBody = 2,

    /// <summary>The <see cref="HttpClient"/>'s own timeout fired, not the SDK's deadline.</summary>
    HttpClientTimeout = 3,
}
