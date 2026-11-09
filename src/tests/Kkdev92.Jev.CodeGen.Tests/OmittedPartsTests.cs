using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>
/// <c>public-surface.json</c> names the parts of the document the SDK does not implement. They are
/// read as absent, and only while the document has them exactly as recorded; the cases where it
/// does not are in <see cref="FailClosedTests"/>.
/// </summary>
public sealed class OmittedPartsTests
{
    [Fact]
    public void OmittedPartsProduceNothing()
    {
        var contract = OpenApiParser.Parse(SpecFixture.Committed());

        Assert.DoesNotContain(contract.Schemas, s => s.WireName is "Asset" or "BoxQuestion" or "BoxAnswer" or "BoxCoordinates");
        Assert.Equal(["choice", "noul", "score"], Tags(contract, "Question"));
        Assert.Equal(["choice", "noul", "score"], Tags(contract, "Answer"));
        Assert.DoesNotContain(((ObjectSchemaDef)contract.Schema("SystemOneRequest")).Properties, p => p.WireName == "assets");

        // The images header is left out; the long-context header beside it is read.
        var systemOne = contract.Operations.Single(o => o.Key.Path == "/v1/systemone");
        Assert.Equal(new HeaderDef("TypeSafe-Danger-Zone-Long-Context", "true"), Assert.Single(systemOne.Headers));
        Assert.Empty(contract.Operations.Single(o => o.Key.Path == "/v1/models").Headers);
    }

    [Fact]
    public void TheCommittedContractStillHasWhatIsOmitted()
    {
        // The parts are left out of the reading, not out of the contract: the semantics checks and the
        // comparison with TypeSafe's live document see the document as TypeSafe publishes it.
        var document = SpecFixture.Document();

        Assert.NotNull(document.Schema("BoxQuestion"));
        Assert.NotNull(document.Property("SystemOneRequest", "assets"));
        Assert.Equal(2, document["paths"]!["/v1/systemone"]!["post"]!["parameters"]!.AsArray().Count);
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
                    .. committed.PublicSurface.Omitted.Where(p => p.Pointer != "/paths/~1v1~1systemone/post/parameters/0"),
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
        var committed = SpecFixture.Committed();
        var snapshot = committed with
        {
            PublicSurface = committed.PublicSurface with { Omitted = [.. committed.PublicSurface.Omitted, committed.PublicSurface.Omitted[0]] },
        };

        var exception = Assert.Throws<InvalidDataException>(() => OpenApiParser.Parse(snapshot));
        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    private static OmittedPart Omit(string pointer, JsonNode? value) => new(pointer, JsonSerializer.SerializeToElement(value), "test");

    private static List<string> Tags(ApiContract contract, string union)
        => ((UnionSchemaDef)contract.Schema(union)).Members.Select(m => m.Tag).Order(StringComparer.Ordinal).ToList();
}
