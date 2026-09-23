using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkdev92.Jev.Tests;

public sealed class JevContentTests
{
    [Fact]
    public void DefaultIsUnspecifiedAndDistinctFromNull()
    {
        JevContent unspecified = default;

        Assert.True(unspecified.IsUnspecified);
        Assert.Equal(JevContentKind.Unspecified, unspecified.Kind);
        Assert.Equal(JevContentKind.Null, JevContent.Null.Kind);
        Assert.NotEqual(unspecified, JevContent.Null);
        Assert.Throws<InvalidOperationException>(() => unspecified.ToJsonElement());
    }

    [Fact]
    public void TextStaysTextEvenWhenItLooksLikeJson()
    {
        var content = JevContent.FromText("""{"x":1}""");

        Assert.Equal(JevContentKind.Text, content.Kind);
        Assert.True(content.TryGetText(out var text));
        Assert.Equal("""{"x":1}""", text);
        Assert.Equal(JsonValueKind.String, content.ToJsonElement().ValueKind);
    }

    [Fact]
    public void AStringConvertsToTextAndANullStringToNull()
    {
        JevContent text = "hello";
        JevContent none = (string?)null;

        Assert.Equal(JevContentKind.Text, text.Kind);
        Assert.Equal(JevContent.Null, none);
        Assert.Throws<ArgumentNullException>(() => JevContent.FromText(null!));
    }

    [Theory]
    [InlineData("""{"b":1,"a":[true,null,1.5]}""", JevContentKind.Object)]
    [InlineData("""[1,"two",{"three":3}]""", JevContentKind.Array)]
    [InlineData("\"text\"", JevContentKind.Text)]
    [InlineData("null", JevContentKind.Null)]
    [InlineData("  { \"spaced\" : true }  ", JevContentKind.Object)]
    public void JsonRootsTheApiAcceptsAreKept(string json, JevContentKind kind)
        => Assert.Equal(kind, JevContent.FromJson(json).Kind);

    [Theory]
    [InlineData("1")]
    [InlineData("-0.5")]
    [InlineData("true")]
    [InlineData("false")]
    public void ABareNumberOrBooleanIsRefused(string json)
        => Assert.Throws<ArgumentException>(() => JevContent.FromJson(json));

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{/*comment*/}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    public void MalformedOrDuplicatedJsonIsRefused(string json)
        => Assert.Throws<ArgumentException>(() => JevContent.FromJson(json));

    [Fact]
    public void ADuplicateSpelledWithAnEscapeIsStillADuplicate()
    {
        var json = "{\"a\":1,\"" + (char)92 + "u0061\":2}";
        Assert.Throws<ArgumentException>(() => JevContent.FromJson(json));
    }

    /// <summary>JsonDocument does not validate UTF-8 inside a string it has not been asked to decode.</summary>
    [Fact]
    public void InvalidUtf8IsRefusedEvenInsideAString()
    {
        byte[] json = [.. "{\"x\":\""u8, 0xC3, 0x28, .. "\"}"u8];
        Assert.Throws<ArgumentException>(() => JevContent.FromJson(json));
    }

    [Fact]
    public void DepthIsBounded()
    {
        var atLimit = new string('[', JevContent.MaxDepth) + new string(']', JevContent.MaxDepth);
        var overLimit = new string('[', JevContent.MaxDepth + 1) + new string(']', JevContent.MaxDepth + 1);

        Assert.Equal(JevContentKind.Array, JevContent.FromJson(atLimit).Kind);
        Assert.Throws<ArgumentException>(() => JevContent.FromJson(overLimit));
    }

    [Fact]
    public void ARefusalNeverQuotesTheInput()
    {
        var exception = Assert.Throws<ArgumentException>(() => JevContent.FromJson("{\"password\":\"hunter2\""));
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentOwnsItsBytes()
    {
        var source = Encoding.UTF8.GetBytes("""{"k":"original"}""");
        var content = JevContent.FromJson(source);

        source.AsSpan().Fill((byte)'x');

        Assert.Equal("original", content.ToJsonElement().GetProperty("k").GetString());
    }

    [Fact]
    public void JsonIsKeptCompact()
    {
        var content = JevContent.FromJson("{ \"a\" : [ 1 , 2 ] }");
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            content.WriteTo(writer);
        }

        Assert.Equal("""{"a":[1,2]}""", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void ATypedValueIsSerializedWithItsOwnMetadata()
    {
        var content = JevContent.FromJson(new Ticket("Duplicate charge", 2), TestJsonContext.Default.Ticket);

        Assert.Equal(JevContentKind.Object, content.Kind);
        Assert.Equal(2, content.ToJsonElement().GetProperty("priority").GetInt32());
    }

    [Fact]
    public void AnElementIsRevalidatedNotTrusted()
    {
        using var document = JsonDocument.Parse("{\"a\":1,\"a\":2}");

        Assert.Throws<ArgumentException>(() => JevContent.FromJsonElement(document.RootElement));
        Assert.Throws<ArgumentException>(() => JevContent.FromJsonElement(default));
    }

    [Fact]
    public void EqualityIsByKindAndValue()
    {
        Assert.Equal(JevContent.FromText("a"), (JevContent)"a");
        Assert.NotEqual(JevContent.FromText("a"), JevContent.FromText("A"));
        Assert.Equal(JevContent.FromJson("{\"a\":1}"), JevContent.FromJson("{ \"a\": 1 }"));
        Assert.NotEqual(JevContent.FromJson("\"a\""), JevContent.FromJson("[\"a\"]"));
        Assert.Equal(JevContent.FromText("a").GetHashCode(), ((JevContent)"a").GetHashCode());
    }

    [Fact]
    public void ToStringNeverShowsTheContent()
    {
        Assert.Equal("Text (7 chars)", JevContent.FromText("hunter2").ToString());
        Assert.Equal("Object (20 bytes)", JevContent.FromJson("""{"secret":"hunter2"}""").ToString());
        Assert.Equal("Null", JevContent.Null.ToString());
        Assert.Equal("Unspecified", default(JevContent).ToString());
    }

    internal sealed record Ticket(string Subject, int Priority);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JevContentTests.Ticket))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
