using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;

namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>
/// Reduces TypeSafe's OpenAPI document to the contract: everything the wire depends on, and none of
/// the prose.
/// </summary>
/// <remarks>
/// <para>
/// The document is served from <c>api.typesafe.ai</c>, and TypeSafe's terms of use (updated
/// 2026-09-19) cover the site "including subdomains" and do not permit "reproducing, distributing,
/// publicly displaying" it. It carries no licence of its own, and its descriptions are TypeSafe's
/// writing. So the repository does not keep a copy of the document. It keeps what an
/// interoperating client cannot avoid knowing — paths, methods, schema and property names, types,
/// required members, bounds, discriminators, security — and records the fetched document's SHA-256
/// and length, which is enough to prove which document the contract came from without
/// redistributing it.
/// </para>
/// <para>
/// Removed, wherever they are keywords: <c>description</c>, <c>summary</c>, <c>title</c>,
/// <c>examples</c>, <c>example</c> and <c>$comment</c>. Kept: <c>info.title</c>, which OpenAPI
/// requires and which is the product's name rather than anyone's prose. None of the removed
/// keywords can change what is sent or received, which is why the parser already read past them;
/// the generated sources do not change when they are removed.
/// </para>
/// <para>
/// "Wherever they are keywords" is the part that needs care. <c>ModelMetadata</c> has a property
/// called <c>description</c>, and under <c>properties</c> that word is a name, not an annotation:
/// removing it would delete a field of the contract. So the document is walked with its structure
/// in mind — a map of names (<c>properties</c>, <c>schemas</c>, <c>paths</c>, <c>responses</c> and
/// the like) keeps every member, a keyword object loses its annotations, and data
/// (<c>enum</c>, <c>const</c>, <c>default</c>, <c>required</c>, extensions) is copied untouched.
/// </para>
/// </remarks>
internal static class ContractExtractor
{
    /// <summary>The annotation keywords removed from every keyword object.</summary>
    public static readonly FrozenSet<string> AnnotationKeywords = FrozenSet.Create(
        StringComparer.Ordinal, "description", "summary", "title", "examples", "example", "$comment");

    /// <summary>Keywords whose value is a map from names the document chose to objects it defines.</summary>
    private static readonly FrozenSet<string> NameMaps = FrozenSet.Create(
        StringComparer.Ordinal,
        "paths", "webhooks", "pathItems", "callbacks",
        "schemas", "responses", "requestBodies", "securitySchemes", "links", "headers",
        "content", "encoding", "variables",
        "properties", "patternProperties", "dependentSchemas", "dependentRequired", "$defs", "definitions",
        "mapping");

    /// <summary>Keywords whose value is data rather than more of the document's structure.</summary>
    private static readonly FrozenSet<string> Data = FrozenSet.Create(
        StringComparer.Ordinal, "enum", "const", "default", "required");

    private enum Position
    {
        /// <summary>An object whose members are keywords: a schema, an operation, a response.</summary>
        Keywords,

        /// <summary>An object whose member names are names, each defining something.</summary>
        Names,

        /// <summary>A value copied as it is.</summary>
        Data,
    }

    /// <summary>Returns the canonical bytes of the contract the document describes.</summary>
    /// <exception cref="InvalidDataException">The document is not valid UTF-8 JSON, or repeats a key.</exception>
    public static byte[] Extract(ReadOnlySpan<byte> document)
    {
        // Canonicalizing first applies every check the canonicalizer makes — UTF-8, no byte order
        // mark, no repeated key — before anything is removed.
        var canonical = JsonCanonicalizer.Canonicalize(document);

        using var parsed = JsonDocument.Parse(canonical);
        var buffer = new ArrayBufferWriter<byte>(canonical.Length);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonCanonicalizer.Encoder, MaxDepth = 128 }))
        {
            Write(parsed.RootElement, writer, Position.Keywords, isInfo: false, isRoot: true);
        }

        return JsonCanonicalizer.Canonicalize(buffer.WrittenSpan);
    }

    /// <summary>True when the bytes are already a canonical contract: nothing left to remove, nothing to reorder.</summary>
    public static bool IsContract(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return Extract(utf8Json).AsSpan().SequenceEqual(utf8Json);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, Position position, bool isInfo, bool isRoot)
    {
        if (position == Position.Data)
        {
            element.WriteTo(writer);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object when position == Position.Names:
                writer.WriteStartObject();

                foreach (var member in element.EnumerateObject())
                {
                    // Every name stays, whatever it is called; what it names is structure again.
                    writer.WritePropertyName(member.Name);
                    Write(member.Value, writer, Position.Keywords, isInfo: false, isRoot: false);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var member in element.EnumerateObject())
                {
                    if (AnnotationKeywords.Contains(member.Name) && !(isInfo && member.Name == "title"))
                    {
                        continue;
                    }

                    writer.WritePropertyName(member.Name);

                    var next = member.Name switch
                    {
                        _ when Data.Contains(member.Name) || member.Name.StartsWith("x-", StringComparison.Ordinal) => Position.Data,
                        _ when NameMaps.Contains(member.Name) && member.Value.ValueKind == JsonValueKind.Object => Position.Names,
                        _ => Position.Keywords,
                    };

                    if (member.Name == "security" && member.Value.ValueKind == JsonValueKind.Array)
                    {
                        // Each security requirement maps scheme names to scopes.
                        writer.WriteStartArray();

                        foreach (var requirement in member.Value.EnumerateArray())
                        {
                            Write(requirement, writer, Position.Names, isInfo: false, isRoot: false);
                        }

                        writer.WriteEndArray();
                        continue;
                    }

                    Write(member.Value, writer, next, isInfo: isRoot && member.Name == "info", isRoot: false);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    // anyOf, oneOf, allOf, prefixItems, parameters, servers, tags: each element is a
                    // keyword object, or a scalar that is copied as it is.
                    Write(item, writer, Position.Keywords, isInfo: false, isRoot: false);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
