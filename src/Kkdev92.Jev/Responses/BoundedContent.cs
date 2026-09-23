namespace Kkdev92.Jev.Responses;

/// <summary>
/// Reads a response body into an owned buffer, counting the bytes that actually arrive.
/// </summary>
/// <remarks>
/// <para>
/// <c>Content-Length</c> is used to refuse early and to size the first buffer, never trusted beyond
/// that: it can be absent, wrong, or describe the compressed form of a body that inflates. The limit
/// applies to the bytes read.
/// </para>
/// <para>
/// The buffer always has room for one byte more than it expects — one more than
/// <c>Content-Length</c>, and at most one more than the limit. So the read that finds the end of an
/// honest body lands in the buffer it already has, instead of forcing a second one twice the size
/// just to learn that nothing followed; and a body that fills the room past the limit is over it,
/// and is refused rather than cut to fit.
/// </para>
/// <para>
/// Reading the whole body before parsing is deliberate. A response is a few kilobytes, the parser
/// then works synchronously over one contiguous span with no reader state carried across awaits,
/// and nothing borrowed from the connection outlives the call. This is not a streaming API and
/// does not claim to be.
/// </para>
/// </remarks>
internal static class BoundedContent
{
    private const int DefaultInitialBytes = 16 * 1024;

    /// <summary>A body that must be complete: over the limit is an error.</summary>
    /// <exception cref="JevLimitException">The body is larger than <paramref name="limit"/>.</exception>
    public static async Task<ArraySegment<byte>> ReadAsync(HttpContent content, int limit, JevOperation operation, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > limit)
        {
            throw new JevLimitException(operation, JevLimit.ResponseBody, limit, JevRequestTransmission.Started);
        }

        var (buffer, length, truncated) = await ReadCoreAsync(content, limit, cancellationToken).ConfigureAwait(false);

        return truncated
            ? throw new JevLimitException(operation, JevLimit.ResponseBody, limit, JevRequestTransmission.Started)
            : new ArraySegment<byte>(buffer, 0, length);
    }

    /// <summary>An error body, where the status matters more than the text: over the limit is truncated, not refused.</summary>
    public static async Task<ArraySegment<byte>> ReadTruncatedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (limit == 0)
        {
            return default;
        }

        var (buffer, length, _) = await ReadCoreAsync(content, limit, cancellationToken).ConfigureAwait(false);
        return new ArraySegment<byte>(buffer, 0, length);
    }

    private static async Task<(byte[] Buffer, int Length, bool Truncated)> ReadCoreAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        // Filling this many bytes proves the body is over the limit.
        var room = (int)Math.Min((long)limit + 1, Array.MaxLength);
        var declared = content.Headers.ContentLength;
        var initial = declared is { } d ? (int)Math.Min(d, room - 1) + 1 : Math.Min(DefaultInitialBytes, room);
        var buffer = new byte[Math.Max(initial, 1)];
        var length = 0;

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= room)
                    {
                        return (buffer, Math.Min(length, limit), true);
                    }

                    var grown = new byte[(int)Math.Min((long)Math.Max(buffer.Length, 4096) * 2, room)];
                    buffer.AsSpan(0, length).CopyTo(grown);
                    buffer = grown;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    return (buffer, length, false);
                }

                length += read;
            }
        }
    }
}
