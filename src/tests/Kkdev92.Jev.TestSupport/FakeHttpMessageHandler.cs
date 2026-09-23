using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kkdev92.Jev.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a function and records every request it saw.
/// </summary>
/// <remarks>
/// The request is copied — method, URI, version, headers and body bytes — before the responder runs,
/// because the client disposes the <see cref="HttpRequestMessage"/> as soon as the call is done, and
/// an assertion made afterwards would read a disposed object.
/// </remarks>
public sealed class FakeHttpMessageHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    /// <summary>Every request, in the order the handler received them.</summary>
    public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

    /// <summary>Answers every request with the same JSON body and status.</summary>
    public static FakeHttpMessageHandler Always(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new((_, _) => Task.FromResult(FakeResponses.Json(json, status)));

    /// <summary>Answers requests in turn from a sequence; a request past the end fails the test that made it.</summary>
    public static FakeHttpMessageHandler Sequence(params Func<HttpResponseMessage>[] responses)
    {
        var next = -1;

        return new((_, _) =>
        {
            var index = Interlocked.Increment(ref next);

            return index < responses.Length
                ? Task.FromResult(responses[index]())
                : throw new InvalidOperationException($"The fake handler was asked for response {index + 1} of {responses.Length}.");
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = await CapturedRequest.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(captured);
        return await responder(captured, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A request as the handler saw it, detached from the message the client disposes.</summary>
public sealed class CapturedRequest
{
    private CapturedRequest(
        HttpMethod method,
        Uri uri,
        Version version,
        HttpVersionPolicy versionPolicy,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> contentHeaders,
        byte[] body,
        HttpRequestMessage original)
    {
        Method = method;
        Uri = uri;
        Version = version;
        VersionPolicy = versionPolicy;
        Headers = headers;
        ContentHeaders = contentHeaders;
        Body = body;
        Original = original;
    }

    public HttpMethod Method { get; }

    public Uri Uri { get; }

    public Version Version { get; }

    public HttpVersionPolicy VersionPolicy { get; }

    /// <summary>Request headers, each joined with ", ", keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Content headers, keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> ContentHeaders { get; }

    public byte[] Body { get; }

    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>The message itself, for identity comparisons only. It is disposed by the time a test reads it.</summary>
    public HttpRequestMessage Original { get; }

    public static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Version,
            request.VersionPolicy,
            Flatten(request.Headers),
            request.Content is null ? new Dictionary<string, string>() : Flatten(request.Content.Headers),
            body,
            request);
    }

    private static Dictionary<string, string> Flatten(HttpHeaders headers)
        => headers.NonValidated.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
}
