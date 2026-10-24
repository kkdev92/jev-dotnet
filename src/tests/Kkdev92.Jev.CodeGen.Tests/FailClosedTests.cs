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
        { "a maximum on score levels other than the pinned one", d => d.Property("ScoreQuestion", "criteria")["maxItems"] = 9 },
        { "a maximum on choice options other than the pinned one", d => d.Property("ChoiceQuestion", "criteria")["maxProperties"] = 254 },
        { "a maximum where nothing pins one", d => d.Property("SystemOneRequest", "questions")["maxProperties"] = 1024 },
        { "x-accepts that disagrees with the anyOf beside it", d => d.Property("NoulQuestion", "instructions")["x-accepts"] = "string" },
        { "x-accepts with no anyOf beside it", d => d.Property("Usage", "input_tokens")["x-accepts"] = "integer" },
        { "an omitted header that became required", d => d["paths"]!["/v1/systemone"]!["post"]!["parameters"]![0]!["required"] = true },
        { "a header beside the omitted ones", d => d["paths"]!["/v1/systemone"]!["post"]!["parameters"]!.AsArray().Add(new JsonObject { ["in"] = "header", ["name"] = "TypeSafe-Something-Else", ["schema"] = new JsonObject { ["type"] = "string" } }) },
        { "an omitted schema the document no longer has", d => d["components"]!["schemas"]!.AsObject().Remove("BoxCoordinates") },
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
        { "a server other than the default base address", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://eu.api.typesafe.ai" }) },
        { "the default base address with a path", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai/v2" }) },
        { "a second server", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai" }, new JsonObject { ["url"] = "https://eu.api.typesafe.ai" }) },
        { "an empty server list", d => d["servers"] = new JsonArray() },
        { "a server with variables", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai", ["variables"] = new JsonObject() }) },
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

    [Fact]
    public void TheServerTheRuntimeAlreadyUsesIsAccepted()
    {
        // The runtime sends every request to the defaultBaseAddress constant, so a document that names
        // that origin, and only that origin, says nothing the SDK does not already do.
        var snapshot = SpecFixture.With(d => d["servers"] = new JsonArray(new JsonObject
        {
            ["url"] = "https://api.typesafe.ai",
            ["description"] = "Production",
        }));

        _ = ContractValidator.Validate(OpenApiParser.Parse(snapshot), snapshot);
    }
}
