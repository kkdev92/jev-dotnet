using System.Text;

namespace Kkdev92.Jev.TestSupport;

/// <summary>
/// A <c>multipart/form-data</c> body taken apart for assertions, strictly: every delimiter where the
/// format puts it, every line ended with CRLF, nothing before the first part or after the last.
/// </summary>
public static class FormBody
{
    private const string MediaTypePrefix = "multipart/form-data; boundary=";

    /// <summary>The boundary a <c>Content-Type</c> header names.</summary>
    /// <exception cref="FormatException">The header is not <c>multipart/form-data</c> with an unquoted boundary.</exception>
    public static string Boundary(string contentType)
        => contentType.StartsWith(MediaTypePrefix, StringComparison.Ordinal) && contentType.Length > MediaTypePrefix.Length
            ? contentType[MediaTypePrefix.Length..]
            : throw new FormatException($"Not a multipart/form-data content type with a boundary: {contentType}");

    /// <summary>The parts of a body, in order.</summary>
    /// <exception cref="FormatException">The body is not laid out as the format requires.</exception>
    public static IReadOnlyList<FormPart> Parse(byte[] body, string contentType)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + Boundary(contentType));
        var span = body.AsSpan();
        var parts = new List<FormPart>();

        if (!span.StartsWith(delimiter) || !span[delimiter.Length..].StartsWith("\r\n"u8))
        {
            throw new FormatException("The body does not start with the boundary and a CRLF.");
        }

        var at = delimiter.Length + 2;

        while (true)
        {
            var headersEnd = span[at..].IndexOf("\r\n\r\n"u8);

            if (headersEnd < 0)
            {
                throw new FormatException("A part's headers do not end with an empty line.");
            }

            var lines = Encoding.ASCII.GetString(span.Slice(at, headersEnd)).Split("\r\n");
            var contentStart = at + headersEnd + 4;

            byte[] next = [.. "\r\n"u8, .. delimiter];
            var contentLength = span[contentStart..].IndexOf(next);

            if (contentLength < 0)
            {
                throw new FormatException("A part is not followed by a delimiter.");
            }

            parts.Add(new FormPart(lines, span.Slice(contentStart, contentLength).ToArray()));
            at = contentStart + contentLength + next.Length;

            if (span[at..].SequenceEqual("--\r\n"u8))
            {
                return parts;
            }

            if (!span[at..].StartsWith("\r\n"u8))
            {
                throw new FormatException("A delimiter is followed by neither a CRLF nor the closing dashes and a CRLF.");
            }

            at += 2;
        }
    }
}

/// <summary>One part of a form: its header lines exactly as sent, and its content.</summary>
public sealed record FormPart(IReadOnlyList<string> HeaderLines, byte[] Content)
{
    /// <summary>The value of a header, matched by name case-insensitively, or <see langword="null"/>.</summary>
    public string? Header(string name)
        => HeaderLines.Select(l => l.Split(": ", 2)).FirstOrDefault(p => p.Length == 2 && string.Equals(p[0], name, StringComparison.OrdinalIgnoreCase))?[1];
}
