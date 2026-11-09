using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Kkdev92.Jev.Requests;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Tests;

/// <summary>Images, the references that stand for them, and the PNG files the tests make.</summary>
public sealed class ImageTests
{
    private static readonly byte[] Png = TestImages.Png(320, 320);

    public static TheoryData<byte[]> NeitherPngNorJpeg => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00 },
        new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 },
        new byte[] { 0xFF, 0xD8 },
        Encoding.ASCII.GetBytes("PNG image"),
    };

    [Fact]
    public void PngAndJpegAreRecognisedByTheirSignatures()
    {
        Assert.Equal(JevImageFormat.Png, JevImage.FromBytes(Png).Format);
        Assert.Equal(JevImageFormat.Jpeg, JevImage.FromBytes(TestImages.JpegSignatureOnly()).Format);
    }

    [Theory]
    [MemberData(nameof(NeitherPngNorJpeg))]
    public void AnythingElseIsRefused(byte[] data)
    {
        var exception = Assert.Throws<ArgumentException>(() => JevImage.FromBytes(data));
        Assert.Equal("data", exception.ParamName);
    }

    [Fact]
    public void TheBytesAreCopiedAndKeptExactly()
    {
        var data = Png.ToArray();
        var image = JevImage.FromBytes(data);
        data[^1] ^= 0xFF;

        Assert.Equal(Png, image.Data);
        Assert.Equal(Png.Length, image.Length);
    }

    [Fact]
    public void EveryImageHasAReferenceOfItsOwnInTheDocumentedShape()
    {
        var first = JevImage.FromBytes(Png);
        var second = JevImage.FromBytes(Png);

        Assert.Matches("^jev-image-[0-9a-f]{32}$", first.Reference);
        Assert.Equal(ImageReferences.Length, first.Reference.Length);
        Assert.NotEqual(first.Reference, second.Reference);
    }

    [Fact]
    public void ToStringNamesTheFormatAndTheSizeButNeverTheReference()
    {
        var image = JevImage.FromBytes(TestImages.JpegSignatureOnly());

        Assert.Equal("JevImage (JPEG, 12 bytes)", image.ToString());
        Assert.DoesNotContain(image.Reference, image.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencesAreFoundAnywhereInText()
    {
        var (a, b) = (ImageReferences.Create(), ImageReferences.Create());
        var found = new HashSet<string>(StringComparer.Ordinal);

        ImageReferences.Collect($"Look at {a}, then at {b}{a}. jev-image- jev-image-{b[10..20]}", found);

        Assert.Equal(new HashSet<string>([a, b]), found);
    }

    [Theory]
    [InlineData("jev-image-")]
    [InlineData("jev-image-0123456789abcdef0123456789abcde")]
    [InlineData("jev-image-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("jev-image-0123456789abcdef0123456789abcdeg")]
    [InlineData("jev-images-0123456789abcdef0123456789abcdef")]
    [InlineData("JEV-IMAGE-0123456789abcdef0123456789abcdef")]
    public void TextThatOnlyResemblesAReferenceIsNotOne(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        ImageReferences.Collect(text, found);
        ImageReferences.CollectFromJson(Encoding.UTF8.GetBytes("\"" + text + "\""), found);

        Assert.Empty(found);
    }

    [Fact]
    public void InJsonOnlyStringValuesAreReadAndEscapesAreDecoded()
    {
        var (inValue, inName, escaped) = (ImageReferences.Create(), ImageReferences.Create(), ImageReferences.Create());

        // The third reference spelled with an escape for its first letter: the same text once decoded.
        var json = "{\"" + inName + "\":[\"see " + inValue + "\",{\"x\":\"" + (char)92 + "u006a" + escaped[1..] + "\"}]}";
        var found = new HashSet<string>(StringComparer.Ordinal);

        ImageReferences.CollectFromJson(Encoding.UTF8.GetBytes(json), found);

        Assert.Equal(new HashSet<string>([inValue, escaped]), found);
    }

    [Fact]
    public void ContentParsedFromJsonKeepsNoEscapeThatWouldHideAReference()
    {
        // The quick check looks for the prefix in the bytes. That is sound because every JSON value
        // it looks at was written by the SDK's own writer, which never escapes a letter.
        var reference = ImageReferences.Create();
        var content = JevContent.FromJson("{\"photo\":\"" + (char)92 + "u006a" + reference[1..] + "\"}");

        Assert.True(ImageReferences.MayContain(content.Json));
    }

    /// <summary>The CRC-32 the generator uses gives the standard check value, and the constant every PNG file ends with.</summary>
    [Fact]
    public void TheTestImagesAreWellFormedPngFiles()
    {
        Assert.Equal(0xCBF43926u, TestImages.Crc32("123456789"u8));
        Assert.Equal(0xAE426082u, BinaryPrimitives.ReadUInt32BigEndian(Png.AsSpan(Png.Length - 4)));

        var png = TestImages.Png(300, 280, (10, 20, 30));
        Assert.True(png.AsSpan().StartsWith(TestImages.PngSignature));
        Assert.Equal(300, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(280, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        // The image data inflates to one filter byte and three bytes a pixel per row, black inside the square.
        var dataLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        Assert.Equal("IDAT", Encoding.ASCII.GetString(png, 37, 4));

        using var inflated = new MemoryStream();

        using (var zlib = new ZLibStream(new MemoryStream(png, 41, dataLength), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var pixels = inflated.ToArray();
        Assert.Equal(280 * (1 + (300 * 3)), pixels.Length);
        Assert.Equal(0, pixels[(25 * 901) + 1 + (15 * 3)]);
        Assert.Equal(255, pixels[(25 * 901) + 1 + (45 * 3)]);
    }
}
