using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.OpenApi;
using Kkdev92.Jev.CodeGen.Validation;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>
/// Anything the generator does not interpret stops generation, rather than being dropped on the way
/// to C#. Each case edits the committed document in one place.
/// </summary>
public sealed class FailClosedTests
{
    public static TheoryData<string, Action<JsonObject>> Unsupported => new()
    {
        { "a maximum on score levels", d => d.Property("ScoreQuestion", "criteria")["maxItems"] = 10 },
        { "a maximum on choice options", d => d.Property("ChoiceQuestion", "criteria")["maxProperties"] = 255 },
        { "a string format", d => d.Property("ModelMetadata", "release_date")["format"] = "date" },
        { "an enum", d => d.Property("ModelMetadata", "name")["enum"] = new JsonArray("jev-latest") },
        { "a numeric bound", d => d.Property("NoulAnswer", "noul")["maximum"] = 1 },
        { "allOf", d => d.Schema("Usage")["allOf"] = new JsonArray() },
        { "an external reference", d => d.Property("SystemOneResponse", "usage")["$ref"] = "https://example.com/usage.json" },
        { "a oneOf without a discriminator", d => d.Schema("Answer").Remove("discriminator") },
        { "a mapping that differs from the branches", d => d["components"]!["schemas"]!["Answer"]!["discriminator"]!["mapping"]!.AsObject().Remove("noul") },
        { "a discriminator constant that disagrees", d => d.Property("NoulAnswer", "type")["const"] = "yesno" },
        { "an api key scheme", d => d["components"]!["securitySchemes"]!["HTTPBearer"]!["type"] = "apiKey" },
        { "OpenAPI 3.0", d => d["openapi"] = "3.0.3" },
        { "a non-JSON response", d => d["paths"]!["/v1/models"]!["get"]!["responses"]!["200"]!["content"]!.AsObject().Add("text/plain", new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } }) },
        { "operation parameters", d => d["paths"]!["/v1/models"]!["get"]!["parameters"] = new JsonArray() },
        { "a typed object inside an anyOf", d => d.Property("SystemOneRequest", "state")["anyOf"]![1]!["additionalProperties"] = new JsonObject { ["type"] = "string" } },
        { "a required property that is not declared", d => d.Schema("Usage")["required"]!.AsArray().Add("cached_tokens") },
        { "a free-form object with a minimum", d => d.Property("ValidationError", "ctx")["minProperties"] = 1 },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void UnsupportedShapesStopGeneration(string what, Action<JsonObject> edit)
    {
        var snapshot = SpecFixture.With(edit);

        var exception = Assert.ThrowsAny<Exception>(() => ContractValidator.Validate(OpenApiParser.Parse(snapshot), snapshot));

        Assert.True(exception is UnsupportedSpecificationException or InvalidDataException, $"{what}: {exception.GetType().Name}: {exception.Message}");
    }

    [Fact]
    public void AnnotationsAreReadPast()
    {
        // Titles, descriptions and examples cannot change the wire, so a new one is not a reason to stop.
        var snapshot = SpecFixture.With(d =>
        {
            d.Property("Usage", "input_tokens")["description"] = "A rewritten description.";
            d.Property("Usage", "input_tokens")["examples"] = new JsonArray(1, 2, 3);
            d.Schema("Usage")["$comment"] = "noted";
        });

        _ = ContractValidator.Validate(OpenApiParser.Parse(snapshot), snapshot);
    }
}
