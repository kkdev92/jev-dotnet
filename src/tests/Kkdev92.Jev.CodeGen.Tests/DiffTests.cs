using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.Commands;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>How a change to the document is classified, judged from the side the data travels.</summary>
public sealed class DiffTests
{
    private static DiffReport Diff(Action<JsonObject> edit)
    {
        var document = SpecFixture.Document();
        edit(document);
        return DiffCommand.Compare(SpecFixture.Committed(), JsonSerializer.SerializeToUtf8Bytes(document));
    }

    /// <summary>The bytes the snapshot was taken from are recognised by their recorded hash.</summary>
    [Fact]
    public void TheSameDocumentIsNoChange()
    {
        // The upstream document is not committed, so the test stands in bytes of its own and
        // records their hash as the upstream one.
        var committed = SpecFixture.Committed();
        var served = committed.CanonicalBytes;
        var snapshot = committed with { Provenance = committed.Provenance with { Upstream = UpstreamFingerprint.Of(served) } };

        var report = DiffCommand.Compare(snapshot, served);

        Assert.True(report.Unchanged);
        Assert.Contains("No change: the document is the one the snapshot was taken from.", report.Lines());
    }

    /// <summary>A different document that yields the same contract changes nothing the SDK depends on.</summary>
    [Fact]
    public void TheSameContractInADifferentDocumentIsAnnotationsOnly()
    {
        var report = DiffCommand.Compare(SpecFixture.Committed(), SpecFixture.Committed().CanonicalBytes);

        Assert.False(report.Unchanged);
        Assert.True(report.AnnotationsOnly);
    }

    [Fact]
    public void AReworderedDescriptionIsAnAnnotationChangeOnly()
    {
        var report = Diff(d => d.Property("Usage", "input_tokens")["description"] = "Reworded.");

        Assert.True(report.AnnotationsOnly);
        Assert.Contains(report.Lines(), l => l.StartsWith("Annotations only (1):", StringComparison.Ordinal));
    }

    [Fact]
    public void AVersionBumpAloneIsReportedAsAnOtherChange()
    {
        var report = Diff(d => d["info"]!["version"] = "0.3.0");

        Assert.True(report.Unclassified);
        Assert.Contains(report.Lines(), l => l.Contains("info.version moved from 0.2.0 to 0.3.0", StringComparison.Ordinal));
        Assert.Contains(report.Lines(), l => l.StartsWith("Other contract change (1):", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewOptionalFieldInAResponseIsAdditive()
    {
        var report = Diff(d => d.Schema("Usage")["properties"]!["cached_tokens"] = new JsonObject { ["type"] = "integer" });

        Assert.Empty(report.Breaking);
        Assert.Contains(report.Additive, l => l.Contains("Usage.cached_tokens was added (optional, received)", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewRequiredFieldInARequestIsBreaking()
    {
        var report = Diff(d =>
        {
            d.Schema("SystemOneRequest")["properties"]!["region"] = new JsonObject { ["type"] = "string" };
            d.Schema("SystemOneRequest")["required"]!.AsArray().Add("region");
        });

        Assert.Contains(report.Breaking, l => l.Contains("SystemOneRequest.region was added (required, sent)", StringComparison.Ordinal));
    }

    [Fact]
    public void AKindTheServiceStopsAcceptingBreaksTheSender()
    {
        var report = Diff(d => d.Property("SystemOneRequest", "state")["anyOf"]!.AsArray().RemoveAt(2));

        Assert.Contains(report.Breaking, l => l.Contains("SystemOneRequest.state no longer admits Array", StringComparison.Ordinal));
    }

    [Fact]
    public void AKindTheServiceStartsSendingBreaksTheReader()
    {
        var report = Diff(d => d.Property("ScoreAnswer", "legend")["additionalProperties"]!["anyOf"]!.AsArray().Add(new JsonObject { ["type"] = "null" }));

        Assert.Contains(report.Breaking, l => l.Contains("ScoreAnswer.legend", StringComparison.Ordinal) && l.Contains("now admits Null", StringComparison.Ordinal));
    }

    [Fact]
    public void ARemovedOperationIsBreakingAndANewOneIsFlagged()
    {
        var removed = Diff(d => d["paths"]!.AsObject().Remove("/v1/models"));
        Assert.Contains(removed.Breaking, l => l.Contains("operation GET /v1/models was removed", StringComparison.Ordinal));

        var added = Diff(d => d["paths"]!["/v1/batch"] = JsonNode.Parse("""{"post":{"responses":{"200":{"description":"ok"}}}}"""));
        Assert.Contains(added.Additive, l => l.Contains("new operation POST /v1/batch", StringComparison.Ordinal));
    }

    [Fact]
    public void AStaleResolutionIsReported()
    {
        var report = Diff(d => d.Schema("ChoiceQuestion")["required"]!.AsArray().Add("instructions"));
        Assert.Contains(report.Breaking, l => l.Contains("instructions-optional", StringComparison.Ordinal));
    }

    [Fact]
    public void SomethingTheGeneratorCannotReadIsReportedAsPotentiallyBreaking()
    {
        var report = Diff(d => d.Property("ScoreQuestion", "criteria")["maxItems"] = 10);

        Assert.Contains(report.Breaking, l => l.Contains("does not interpret", StringComparison.Ordinal));
        Assert.Contains(report.Lines(), l => l.StartsWith("Potentially breaking (1):", StringComparison.Ordinal));
    }

    [Fact]
    public void ATighterMinimumOnARequestIsBreaking()
    {
        var report = Diff(d => d.Property("ScoreQuestion", "criteria")["minItems"] = 2);
        Assert.Contains(report.Breaking, l => l.Contains("minItems moved from 1 to 2", StringComparison.Ordinal));
    }
}
