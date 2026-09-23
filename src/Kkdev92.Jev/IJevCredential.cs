namespace Kkdev92.Jev;

/// <summary>
/// Supplies the API key for a request.
/// </summary>
/// <remarks>
/// <para>
/// Asked once per attempt, immediately before the request is sent, so a key rotated between a
/// failure and its retry is picked up. The key is placed on that one request and nowhere else: the
/// client never writes it to <c>HttpClient.DefaultRequestHeaders</c>, where it would outlive the
/// request and be shared with every other caller of the same <see cref="HttpClient"/>.
/// </para>
/// <para>
/// Caching, refreshing and retrying a secret store are the implementation's business. The client
/// awaits the returned task once and does not retry a provider that throws.
/// </para>
/// </remarks>
public interface IJevCredential
{
    /// <summary>Returns the API key to send.</summary>
    /// <param name="cancellationToken">Cancelled when the call is cancelled or its deadline passes.</param>
    /// <returns>
    /// The key: printable ASCII with no whitespace. Anything else is refused before it is sent, and
    /// the key is never included in the resulting exception.
    /// </returns>
    ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken);
}
