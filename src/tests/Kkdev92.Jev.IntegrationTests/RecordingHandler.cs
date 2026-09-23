using System.Collections.Concurrent;

namespace Kkdev92.Jev.IntegrationTests;

/// <summary>
/// What a live test saw beneath the client: how many requests left the process, the HTTP version
/// each response came back over, and the last response body.
/// </summary>
/// <remarks>
/// The body is kept for one purpose: when the SDK refuses a response, the report can still say
/// what shape it had. The SDK itself never hands a refused body back — its exceptions carry no
/// content — so this is the only place a keyed run can learn why.
/// </remarks>
internal sealed class Recording
{
    private int _requests;
    private byte[]? _lastBody;

    /// <summary>How many requests were sent, retries included.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>The protocol version of each response, in the order they arrived.</summary>
    public ConcurrentQueue<Version> Versions { get; } = new();

    /// <summary>The body of the last response, whatever its status.</summary>
    public byte[]? LastBody => Volatile.Read(ref _lastBody);

    internal void Sent() => Interlocked.Increment(ref _requests);

    internal void Received(Version version, byte[] body)
    {
        Versions.Enqueue(version);
        Volatile.Write(ref _lastBody, body);
    }
}

/// <summary>Records into a <see cref="Recording"/> every exchange that passes through it.</summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly Recording _recording;

    /// <summary>For <c>IHttpClientFactory</c>, which supplies the inner handler itself.</summary>
    public RecordingHandler(Recording recording) => _recording = recording;

    public RecordingHandler(Recording recording, HttpMessageHandler inner)
        : base(inner) => _recording = recording;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _recording.Sent();

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Buffered, so the client above still reads the body in full.
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        _recording.Received(response.Version, await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

        return response;
    }
}
