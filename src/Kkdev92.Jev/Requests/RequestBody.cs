using System.Globalization;
using System.Net;
using System.Text;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Requests;

/// <summary>
/// The body of one evaluation: written once, before the first attempt, and sent unchanged by every attempt.
/// </summary>
/// <remarks>
/// <para>
/// A JSON body for a request without images. With images, a <c>multipart/form-data</c> body: the
/// JSON as its first part, named <c>request</c>, and each image as a file part of its own. TypeSafe
/// recommends that over base64 in the JSON, which makes every image about a third larger.
/// </para>
/// <para>
/// Each attempt gets a new <see cref="HttpContent"/>, because a content is disposed with its
/// request, but every content is over the same bytes: nothing is serialized again for a retry.
/// </para>
/// </remarks>
internal sealed class RequestBody
{
    private readonly ReadOnlyMemory<byte>[]? _segments;
    private readonly string _contentType;

    private RequestBody(byte[] json, ReadOnlyMemory<byte>[]? segments, int length, string contentType)
    {
        Json = json;
        _segments = segments;
        _contentType = contentType;
        Length = length;
    }

    /// <summary>The JSON of the request: the whole body, or the first part of a form.</summary>
    public byte[] Json { get; }

    /// <summary>The size of the body in bytes, as sent.</summary>
    public int Length { get; }

    /// <summary>Whether the body is a form, which is how images are sent.</summary>
    public bool HasImages => _segments is not null;

    /// <summary>A JSON body.</summary>
    public static RequestBody FromJson(byte[] json) => new(json, null, json.Length, "application/json");

    /// <summary>A form body from its pieces, in the order they are sent.</summary>
    public static RequestBody FromForm(byte[] json, ReadOnlyMemory<byte>[] segments, int length, string boundary)
        => new(json, segments, length, "multipart/form-data; boundary=" + boundary);

    /// <summary>A new content over the body's bytes, for one attempt.</summary>
    public HttpContent CreateContent()
    {
        HttpContent content = _segments is null ? new ByteArrayContent(Json) : new SegmentedContent(_segments, Length);

        // Fixed values, so added without validation, as the request's own headers are.
        content.Headers.TryAddWithoutValidation("Content-Type", _contentType);
        return content;
    }

    /// <summary>Content written from pieces that are never joined into one array.</summary>
    private sealed class SegmentedContent(ReadOnlyMemory<byte>[] segments, long totalLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (var segment in segments)
            {
                await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(segment.Span);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = totalLength;
            return true;
        }
    }
}

/// <summary>
/// The parts of a <c>multipart/form-data</c> body around its JSON: the boundaries, each part's
/// headers, and the images themselves.
/// </summary>
/// <remarks>
/// <para>
/// The form is laid out before the JSON is written, because the JSON names the parts in its
/// <c>assets</c> and has to fit in what the parts leave of the body limit. The boundary is chosen
/// after, once the JSON exists to be checked against it.
/// </para>
/// <para>
/// The shape follows TypeSafe's rules for the form: the JSON first, as <c>application/json</c>,
/// under the part name the contract gives; then one file part per image, under a name of its own,
/// with the image's media type and a file name, without which a form parser takes the part for a
/// text field. The images are referenced, not copied.
/// </para>
/// </remarks>
internal sealed class FormLayout
{
    private const string BoundaryPrefix = "jev-";

    private static readonly int BoundaryLength = BoundaryPrefix.Length + 32;

    private readonly JevImage[] _images;
    private readonly string[] _partNames;

    public FormLayout(JevImage[] images)
    {
        _images = images;
        _partNames = new string[images.Length];
        long imageBytes = 0;

        for (var i = 0; i < images.Length; i++)
        {
            _partNames[i] = "image-" + (i + 1).ToString(CultureInfo.InvariantCulture);
            imageBytes += images[i].Length;
        }

        ImageBytes = imageBytes;

        // Every boundary is the same length, so any one of them gives the size of the rest.
        long overhead = 0;

        foreach (var piece in Pieces(new string('0', BoundaryLength)))
        {
            overhead += piece.Length;
        }

        Overhead = overhead;
    }

    /// <summary>The images, in the order their parts are sent.</summary>
    public JevImage[] Images => _images;

    /// <summary>The name of the part that carries each image, by position.</summary>
    public IReadOnlyList<string> PartNames => _partNames;

    /// <summary>The bytes of all the images together.</summary>
    public long ImageBytes { get; }

    /// <summary>The bytes of the boundaries and part headers together.</summary>
    public long Overhead { get; }

    /// <summary>The body: the pieces around <paramref name="json"/>, under a boundary that occurs in none of the parts.</summary>
    public RequestBody Complete(byte[] json)
    {
        string boundary;

        do
        {
            boundary = BoundaryPrefix + Guid.NewGuid().ToString("N");
        }
        while (Occurs(boundary, json));

        var pieces = Pieces(boundary);
        var segments = new ReadOnlyMemory<byte>[(2 * _images.Length) + 3];
        var at = 0;

        segments[at++] = pieces[0];
        segments[at++] = json;

        for (var i = 0; i < _images.Length; i++)
        {
            segments[at++] = pieces[1 + i];
            segments[at++] = _images[i].Data;
        }

        segments[at] = pieces[^1];

        return RequestBody.FromForm(json, segments, checked((int)(Overhead + json.Length + ImageBytes)), boundary);
    }

    /// <summary>Whether the boundary occurs in the JSON or in an image, where it would end a part early.</summary>
    private bool Occurs(string boundary, byte[] json)
    {
        Span<byte> bytes = stackalloc byte[BoundaryLength];
        Encoding.ASCII.GetBytes(boundary, bytes);

        if (json.AsSpan().IndexOf(bytes) >= 0)
        {
            return true;
        }

        foreach (var image in _images)
        {
            if (image.Data.AsSpan().IndexOf(bytes) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pieces between the parts' contents: the JSON part's opening, each image part's opening,
    /// and the closing delimiter. Every line ends with CRLF, as the format requires.
    /// </summary>
    private byte[][] Pieces(string boundary)
    {
        var pieces = new byte[_images.Length + 2][];

        pieces[0] = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"{JevContract.SystemOneFormJsonPart}\"\r\nContent-Type: application/json\r\n\r\n");

        for (var i = 0; i < _images.Length; i++)
        {
            var (extension, mediaType) = _images[i].Format == JevImageFormat.Png ? ("png", "image/png") : ("jpg", "image/jpeg");

            pieces[1 + i] = Encoding.ASCII.GetBytes(
                $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"{_partNames[i]}\"; filename=\"{_partNames[i]}.{extension}\"\r\nContent-Type: {mediaType}\r\n\r\n");
        }

        pieces[^1] = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        return pieces;
    }
}
