using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.Normalization;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Specifications;
using Kkdev92.Jev.CodeGen.Validation;

namespace Kkdev92.Jev.CodeGen.Tests;

public sealed class ValidatorTests
{
    private static ValidatedContract Validate(SpecSnapshot snapshot) => ContractValidator.Validate(OpenApiParser.Parse(snapshot), snapshot);

    [Fact]
    public void TheCommittedContractValidatesAndReachesEverySchema()
    {
        var validated = Validate(SpecFixture.Committed());

        Assert.Equal(["GET /v1/models", "POST /v1/systemone"], validated.Operations.Select(o => o.Definition.Key.ToString()));
        Assert.Equal(16, validated.ReachableSchemas.Count);
        Assert.Empty(validated.Warnings);
    }

    /// <summary>An operation is exposed because public-surface.json says so, never because the document contains it.</summary>
    [Fact]
    public void ANewOperationStopsGenerationUntilItIsClassified()
    {
        var snapshot = SpecFixture.With(d => d["paths"]!["/v1/batch"] = JsonNode.Parse(
            """{"post":{"responses":{"200":{"description":"ok","content":{"application/json":{"schema":{"$ref":"#/components/schemas/SystemOneResponse"}}}}}}}"""));

        var exception = Assert.Throws<InvalidDataException>(() => Validate(snapshot));
        Assert.Contains("POST /v1/batch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcludedOperationIsNotGenerated()
    {
        var snapshot = SpecFixture.With(d => d["paths"]!["/v1/batch"] = JsonNode.Parse(
            """{"post":{"responses":{"200":{"description":"ok","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Usage"}}}}}}}"""));

        snapshot = snapshot with { PublicSurface = snapshot.PublicSurface with { Excluded = [new OperationKey("POST", "/v1/batch")] } };

        Assert.Equal(2, Validate(snapshot).Operations.Count);
    }

    [Fact]
    public void AnApprovedOperationThatDisappearedIsAnError()
    {
        var snapshot = SpecFixture.With(d => d["paths"]!.AsObject().Remove("/v1/models"));
        Assert.Throws<InvalidDataException>(() => Validate(snapshot));
    }

    /// <summary>
    /// semantics.json resolved the instructions conflict on the basis that the document makes them
    /// optional. When that stops being true, the resolution is stale and generation says so.
    /// </summary>
    [Fact]
    public void AResolutionWhoseBasisChangedStopsGeneration()
    {
        var snapshot = SpecFixture.With(d => d.Schema("NoulQuestion")["required"]!.AsArray().Add("instructions"));

        var exception = Assert.Throws<InvalidDataException>(() => Validate(snapshot));
        Assert.Contains("instructions-optional", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoWireNamesThatBecomeOneIdentifierAreAnError()
    {
        var snapshot = SpecFixture.With(d => d.Schema("Usage")["properties"]!["input-tokens"] = new JsonObject { ["type"] = "integer" });
        Assert.Throws<InvalidDataException>(() => Validate(snapshot));
    }

    [Fact]
    public void AStaleNamingOverrideIsAnError()
    {
        var snapshot = SpecFixture.Committed();
        snapshot = snapshot with { NamingOverrides = new NamingOverrides(new Dictionary<string, string> { ["NoSuchSchema"] = "Renamed" }) };

        Assert.Throws<InvalidDataException>(() => Validate(snapshot));
    }

    [Theory]
    [InlineData("/components/schemas/Usage/required", "contains", "\"input_tokens\"", true)]
    [InlineData("/components/schemas/Usage/required", "notContains", "\"cached_tokens\"", true)]
    [InlineData("/components/schemas/ScoreQuestion/properties/criteria/minItems", "equals", "1", true)]
    [InlineData("/components/schemas/ScoreQuestion/properties/criteria/minItems", "equals", "2", false)]
    [InlineData("/paths/~1v1~1systemone/post", "present", null, true)]
    [InlineData("/paths/~1v1~1systemone/put", "absent", null, true)]
    [InlineData("/paths/~1v1~1systemone/post", "absent", null, false)]
    public void ChecksAreEvaluatedAgainstTheDocument(string pointer, string op, string? operand, bool holds)
    {
        using var document = JsonDocument.Parse(SpecFixture.Committed().CanonicalBytes);
        var check = new SpecCheck(pointer, op, operand is null ? null : JsonDocument.Parse(operand).RootElement.Clone());

        Assert.Equal(holds, ContractValidator.Holds(document.RootElement, check));
    }

    [Theory]
    [InlineData("input_tokens", "InputTokens")]
    [InlineData("release_date", "ReleaseDate")]
    [InlineData("true", "True")]
    [InlineData("a-b", "AB")]
    [InlineData("9lives", "_9lives")]
    [InlineData("loc", "Loc")]
    public void WireNamesBecomeIdentifiersByAFixedRule(string wire, string identifier)
        => Assert.Equal(identifier, Naming.ToPascalCase(wire));

    [Theory]
    [InlineData("")]
    [InlineData("__")]
    [InlineData("naïve")]
    [InlineData("a$b")]
    public void NamesTheRuleCannotMapAreErrors(string wire)
        => Assert.Throws<InvalidDataException>(() => Naming.ToPascalCase(wire));
}
