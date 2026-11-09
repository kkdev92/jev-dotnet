using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Kkdev92.Jev.TestSupport;

/// <summary>
/// Small, valid PNG files made on the spot, so that no test depends on a binary file in the repository.
/// </summary>
/// <remarks>
/// Eight-bit RGB, no interlacing, every row unfiltered: the simplest image the PNG format allows,
/// written chunk by chunk with the CRC-32 the format defines. Large enough images — TypeSafe accepts
/// 280 to 1568 pixels a side — compress to a few kilobytes, since they are mostly one colour.
/// </remarks>
public static class TestImages
{
    /// <summary>The eight bytes every PNG file starts with.</summary>
    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A white image with a black square in it.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="square">The square's left, top and side in pixels, or <see langword="null"/> for a plain white image.</param>
    public static byte[] Png(int width, int height, (int Left, int Top, int Side)? square = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var stride = 1 + (width * 3);
        var pixels = new byte[height * stride];

        for (var y = 0; y < height; y++)
        {
            // Filter type 0, then the row as it is.
            pixels[y * stride] = 0;

            for (var x = 0; x < width; x++)
            {
                var inside = square is { } s && x >= s.Left && x < s.Left + s.Side && y >= s.Top && y < s.Top + s.Side;
                pixels.AsSpan((y * stride) + 1 + (x * 3), 3).Fill(inside ? (byte)0 : (byte)255);
            }
        }

        using var compressed = new MemoryStream();

        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;   // bit depth
        header[9] = 2;   // colour type: RGB
        header[10] = 0;  // compression: deflate
        header[11] = 0;  // filter method: adaptive
        header[12] = 0;  // no interlacing

        using var png = new MemoryStream();
        png.Write(PngSignature);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// The first bytes of a JPEG file: enough for the SDK to recognise the format, which is all it
    /// reads. Not an image a decoder would accept, so never sent to the service.
    /// </summary>
    public static byte[] JpegSignatureOnly() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01];

    /// <summary>The CRC-32 the PNG format puts after each chunk: ISO 3309, reflected, over the chunk's type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in bytes)
        {
            crc ^= b;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        stream.Write(word);

        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData.AsSpan(4));
        stream.Write(typeAndData);

        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typeAndData));
        stream.Write(word);
    }
}
