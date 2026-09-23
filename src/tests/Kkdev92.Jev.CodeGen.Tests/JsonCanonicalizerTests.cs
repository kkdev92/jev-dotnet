using System.Text;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

public sealed class JsonCanonicalizerTests
{
    private static string Canonical(string json) => Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void KeysAreSortedOrdinallyAndArraysKeepTheirOrder()
        => Assert.Equal("{\n  \"B\": 1,\n  \"a\": [\n    3,\n    1,\n    2\n  ],\n  \"b\": {}\n}\n", Canonical("""{"b":{},"a":[3,1,2],"B":1}"""));

    [Fact]
    public void NumbersKeepTheirExactSpelling()
        => Assert.Equal("[\n  1.0,\n  1e3,\n  -0,\n  0.1000\n]\n", Canonical("[1.0,1e3,-0,0.1000]"));

    [Fact]
    public void StringsAreReadableNotEscapedForHtml()
    {
        var canonical = Canonical("""{"d":"Bearer <API_KEY> & 'quoted' 日本語"}""");
        Assert.Contains("Bearer <API_KEY> & 'quoted' 日本語", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyContainersStayOnOneLine() => Assert.Equal("{\n  \"a\": [],\n  \"b\": {}\n}\n", Canonical("""{"b":{},"a":[]}"""));

    [Fact]
    public void CanonicalizingIsIdempotent()
    {
        var once = JsonCanonicalizer.Canonicalize("""{"z":[{"b":1,"a":2}],"y":"x"}"""u8);
        Assert.Equal(once, JsonCanonicalizer.Canonicalize(once));
        Assert.True(JsonCanonicalizer.IsCanonical(once));
    }

    [Fact]
    public void TheOutputIsLfWhateverThePlatform()
        => Assert.DoesNotContain('\r', Canonical("""{"a":{"b":[1,2]}}"""));

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{/*c*/"a":1}""")]
    [InlineData("""{"a":1""")]
    [InlineData("""{"a":1} {}""")]
    public void AnythingThatIsNotOneStrictDocumentIsRefused(string json)
        => Assert.Throws<InvalidDataException>(() => JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void InvalidUtf8AndAByteOrderMarkAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => JsonCanonicalizer.Canonicalize([.. "{\"a\":\""u8, 0xC3, 0x28, .. "\"}"u8]));
        Assert.Throws<InvalidDataException>(() => JsonCanonicalizer.Canonicalize([0xEF, 0xBB, 0xBF, .. "{}"u8]));
        Assert.False(JsonCanonicalizer.IsCanonical([0xEF, 0xBB, 0xBF, .. "{}\n"u8]));
    }
}
