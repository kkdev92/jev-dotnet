using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>
/// <c>public-surface.json</c> can name parts of the document the SDK does not implement. They are
/// read as absent, and only while the document has them exactly as recorded.
/// </summary>
/// <remarks>
/// Nothing is omitted from the committed contract, so these tests omit what the SDK left out until
/// it sent images: the images header, the form, the assets and the box question and answer.
/// </remarks>
public sealed class OmittedPartsTests
{
    /// <summary>The parts the SDK omitted before it sent images, by pointer.</summary>
    private static readonly string[] ImageParts =
    [
        "/paths/~1v1~1systemone/post/parameters/0",
        "/paths/~1v1~1systemone/post/requestBody/content/multipart~1form-data",
        "/components/schemas/SystemOneRequest/properties/assets",
        "/components/schemas/Question/discriminator/mapping/box",
        "/components/schemas/Answer/discriminator/mapping/box",
        "/components/schemas/Asset",
        "/components/schemas/BoxQuestion",
        "/components/schemas/BoxAnswer",
        "/components/schemas/BoxCoordinates",
    ];

    [Fact]
    public void NothingIsOmittedFromTheCommittedContract()
    {
        var contract = OpenApiParser.Parse(SpecFixture.Committed());

        Assert.Empty(SpecFixture.Committed().PublicSurface.Omitted);
        Assert.Contains(contract.Schemas, s => s.WireName == "BoxCoordinates");
        Assert.Equal(["box", "choice", "noul", "score"], Tags(contract, "Question"));
        Assert.Equal(["box", "choice", "noul", "score"], Tags(contract, "Answer"));

        var systemOne = contract.Operations.Single(o => o.Key.Path == "/v1/systemone");
        Assert.Equal(["TypeSafe-Danger-Zone-Images", "TypeSafe-Danger-Zone-Long-Context"], systemOne.Headers.Select(h => h.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new FormDef("request", "image/*"), systemOne.Form);
    }

    [Fact]
    public void OmittedPartsProduceNothing()
    {
        var document = SpecFixture.Document();
        var contract = OpenApiParser.Parse(Omitting([.. ImageParts.Select(p => Omit(p, At(document, p)))]));

        Assert.DoesNotContain(contract.Schemas, s => s.WireName is "Asset" or "BoxQuestion" or "BoxAnswer" or "BoxCoordinates");
        Assert.Equal(["choice", "noul", "score"], Tags(contract, "Question"));
        Assert.Equal(["choice", "noul", "score"], Tags(contract, "Answer"));
        Assert.DoesNotContain(((ObjectSchemaDef)contract.Schema("SystemOneRequest")).Properties, p => p.WireName == "assets");

        // The images header and the form are left out; the long-context header beside them is read.
        var systemOne = contract.Operations.Single(o => o.Key.Path == "/v1/systemone");
        Assert.Equal(new HeaderDef("TypeSafe-Danger-Zone-Long-Context", "true"), Assert.Single(systemOne.Headers));
        Assert.Null(systemOne.Form);
        Assert.Empty(contract.Operations.Single(o => o.Key.Path == "/v1/models").Headers);
    }

    [Fact]
    public void AnOmittedPartThatChangedIsAnError()
    {
        // Recorded with a value the document does not have: the part is no longer the one reviewed.
        var recorded = Omit(ImageParts[0], JsonNode.Parse("""{"in":"header","name":"TypeSafe-Danger-Zone-Images","schema":{"type":"string"}}"""));

        var exception = Assert.Throws<InvalidDataException>(() => OpenApiParser.Parse(Omitting([recorded])));
        Assert.Contains("Decide again", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every pointer names an element by its place in the document as committed, so leaving out the
    /// first element of a list does not move the second out from under the pointer that names it.
    /// </summary>
    [Fact]
    public void ElementsOfOneListAreNamedByTheirPlaceInTheCommittedDocument()
    {
        var committed = SpecFixture.Committed();
        var parameters = SpecFixture.Document()["paths"]!["/v1/systemone"]!["post"]!["parameters"]!.AsArray();
        var snapshot = committed with
        {
            PublicSurface = committed.PublicSurface with
            {
                Operations = [.. committed.PublicSurface.Operations.Select(o => o with { Headers = [] })],
                Omitted =
                [
                    Omit("/paths/~1v1~1systemone/post/parameters/0", parameters[0]),
                    Omit("/paths/~1v1~1systemone/post/parameters/1", parameters[1]),
                ],
            },
        };

        var contract = OpenApiParser.Parse(snapshot);

        Assert.All(contract.Operations, o => Assert.Empty(o.Headers));
    }

    [Fact]
    public void APartOmittedTwiceIsAnError()
    {
        var part = Omit(ImageParts[0], At(SpecFixture.Document(), ImageParts[0]));

        var exception = Assert.Throws<InvalidDataException>(() => OpenApiParser.Parse(Omitting([part, part])));
        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The committed snapshot with these parts omitted, and the images header no longer approved.</summary>
    private static SpecSnapshot Omitting(IReadOnlyList<OmittedPart> parts)
    {
        var committed = SpecFixture.Committed();

        return committed with
        {
            PublicSurface = committed.PublicSurface with
            {
                Operations = [.. committed.PublicSurface.Operations.Select(o => o with { Headers = [.. o.Headers.Where(h => h.Name != "TypeSafe-Danger-Zone-Images")] })],
                Omitted = parts,
            },
        };
    }

    /// <summary>The node a JSON pointer names.</summary>
    private static JsonNode? At(JsonObject document, string pointer)
    {
        JsonNode? node = document;

        foreach (var token in pointer.Split('/').Skip(1))
        {
            var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node is JsonArray list ? list[int.Parse(name, CultureInfo.InvariantCulture)] : node![name];
        }

        return node;
    }

    private static OmittedPart Omit(string pointer, JsonNode? value) => new(pointer, JsonSerializer.SerializeToElement(value), "test");

    private static List<string> Tags(ApiContract contract, string union)
        => ((UnionSchemaDef)contract.Schema(union)).Members.Select(m => m.Tag).Order(StringComparer.Ordinal).ToList();
}
