using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.OpenApi;

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
    }

    [Fact]
    public void TheCommittedContractStillHasWhatIsOmitted()
    {
        // The parts are left out of the reading, not out of the contract: the semantics checks and the
        // comparison with TypeSafe's live document see the document as TypeSafe publishes it.
        var document = SpecFixture.Document();

        Assert.NotNull(document.Schema("BoxQuestion"));
        Assert.NotNull(document.Property("SystemOneRequest", "assets"));
        Assert.NotNull(document["paths"]!["/v1/systemone"]!["post"]!["parameters"]);
    }

    private static List<string> Tags(ApiContract contract, string union)
        => ((UnionSchemaDef)contract.Schema(union)).Members.Select(m => m.Tag).Order(StringComparer.Ordinal).ToList();
}
