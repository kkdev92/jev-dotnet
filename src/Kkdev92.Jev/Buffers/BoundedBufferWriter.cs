using System.Buffers;

namespace Kkdev92.Jev.Buffers;

/// <summary>
/// A growable buffer that refuses to hold more than a limit, for writing a request body once.
/// </summary>
/// <remarks>
/// <para>
/// Pooled while it is being written, and never after. <see cref="System.Text.Json.Utf8JsonWriter"/> asks for room
/// for the worst case — three bytes for every character of a string it is about to write — so a
/// buffer sized for the body that will actually be written is grown, and grown again, to satisfy
/// a request it will not use. Borrowing that room from <see cref="ArrayPool{T}.Shared"/> costs no
/// allocation; <see cref="ToArray"/> then copies the body out into an array of exactly its size,
/// which is what the call keeps.
/// </para>
/// <para>
/// The copy is what makes the pool safe here. A request body lives for the whole call — across
/// every attempt, until the last write of the last attempt has finished — and a pooled array
/// returned while an <see cref="HttpContent"/> was still writing it would be read by the next
/// borrower. The body the call sends is never pooled; only the scratch it was written in is, and
/// that goes back before the first attempt starts.
/// </para>
/// <para>
/// Returned cleared. The scratch held the caller's state, and the shared pool hands arrays to
/// anything in the process that asks.
/// </para>
/// <para>
/// The limit is checked as data is committed, so a serializer writing a huge value is stopped within
/// one chunk of the limit rather than after producing the whole thing.
/// </para>
/// </remarks>
internal sealed class BoundedBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly int _limit;
    private readonly Action _onLimit;
    private byte[] _buffer;
    private int _written;

    /// <param name="initialCapacity">A starting size; grown by doubling.</param>
    /// <param name="limit">The most bytes that may be written.</param>
    /// <param name="onLimit">Throws the exception that reports the limit. Must not return.</param>
    public BoundedBufferWriter(int initialCapacity, int limit, Action onLimit)
    {
        _limit = limit;
        _onLimit = onLimit;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(initialCapacity, 256, Math.Max(256, limit)));
    }

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>The body as an array the caller owns outright, exactly as long as what was written.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Advanced past the end of the buffer.");
        }

        _written += count;

        if (_written > _limit)
        {
            _onLimit();
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;

        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void Ensure(int sizeHint)
    {
        var needed = Math.Max(sizeHint, 1);

        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        // The data already written plus what is asked for, checked, never past what an array holds.
        var required = checked(_written + needed);

        if (required > Array.MaxLength)
        {
            _onLimit();
        }

        var size = (int)Math.Min(Math.Max((long)_buffer.Length * 2, required), Array.MaxLength);
        var grown = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = grown;
    }
}
