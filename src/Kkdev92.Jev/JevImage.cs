using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Requests;

namespace Kkdev92.Jev;

/// <summary>
/// A PNG or JPEG image to send with an evaluation, and the reference that stands for it in text.
/// </summary>
/// <remarks>
/// <para>
/// Write <see cref="Reference"/> wherever the image belongs — as a whole string value or inside a
/// longer one, in the state, in an instruction or in a criterion — and send the image with the
/// request: add it to the plan with <see cref="JevDecisionPlanBuilder.AddImage"/> when a question
/// shows it, or pass it in the <c>images</c> of a <see cref="JevClient"/> evaluate method when the
/// state shows it. The service reads the image in the reference's place. A reference in an object
/// key stays text.
/// </para>
/// <para>
/// The bytes are sent as they are: nothing here decodes, resizes, rotates or re-encodes them.
/// TypeSafe accepts still PNG and JPEG images from 280 to 1568 pixels a side, up to 16 MiB each,
/// ignores EXIF orientation, and may change those limits between releases. The service, not this
/// class, refuses an image outside them.
/// </para>
/// <para>
/// Immutable and safe to share. The bytes are copied when the image is created. The reference is
/// random and belongs to this instance: two images made from the same bytes have different ones.
/// </para>
/// </remarks>
[Experimental(Experiments.Images)]
public sealed class JevImage
{
    private JevImage(byte[] data, JevImageFormat format)
    {
        Data = data;
        Format = format;
        Reference = ImageReferences.Create();
    }

    /// <summary>The file format, read from the image's first bytes.</summary>
    public JevImageFormat Format { get; }

    /// <summary>The size of the file, in bytes.</summary>
    public int Length => Data.Length;

    /// <summary>
    /// The text that stands for this image: <c>jev-image-</c> followed by 32 lowercase hexadecimal
    /// digits, random, and unlikely to occur in text by accident.
    /// </summary>
    public string Reference { get; }

    /// <summary>The file, exactly as given.</summary>
    internal byte[] Data { get; }

    /// <summary>Creates an image from the bytes of a PNG or JPEG file.</summary>
    /// <param name="data">The whole file. Copied: changing the caller's buffer afterwards changes nothing.</param>
    /// <returns>The image, with a new reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> does not start with the signature of a PNG or a JPEG file.</exception>
    public static JevImage FromBytes(ReadOnlySpan<byte> data)
    {
        var format = data switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => JevImageFormat.Png,
            [0xFF, 0xD8, 0xFF, ..] => JevImageFormat.Jpeg,
            _ => throw new ArgumentException(
                "The data is neither a PNG nor a JPEG file: it does not start with the signature of either. Convert other formats, such as GIF, WebP or HEIC, before sending them.",
                nameof(data)),
        };

        return new JevImage(data.ToArray(), format);
    }

    /// <summary>The format and the size. Never the reference or the bytes.</summary>
    public override string ToString()
        => $"JevImage ({(Format == JevImageFormat.Png ? "PNG" : "JPEG")}, {Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
}

/// <summary>The file format of a <see cref="JevImage"/>.</summary>
[Experimental(Experiments.Images)]
public enum JevImageFormat
{
    /// <summary>PNG. Sent as <c>image/png</c>.</summary>
    Png = 1,

    /// <summary>JPEG. Sent as <c>image/jpeg</c>.</summary>
    Jpeg = 2,
}
