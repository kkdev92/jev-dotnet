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
        { "the images header, become required", d => Parameters(d)[0]!["required"] = true },
        { "a header nobody classified", d => Parameters(d).Add(new JsonObject { ["in"] = "header", ["name"] = "TypeSafe-Something-Else", ["schema"] = new JsonObject { ["type"] = "string" } }) },
        { "an approved header that became required", d => Parameters(d)[1]!["required"] = true },
        { "an approved header that takes any string", d => Parameters(d)[1]!["schema"] = new JsonObject { ["type"] = "string" } },
        { "an approved header with a second value", d => Parameters(d)[1]!["schema"]!["enum"]!.AsArray().Add("false") },
        { "an approved header the document dropped", d => Parameters(d).RemoveAt(1) },
        { "a switch header nobody classified", d => Parameters(d).Add(new JsonObject { ["in"] = "header", ["name"] = "TypeSafe-Danger-Zone-Other", ["schema"] = new JsonObject { ["enum"] = new JsonArray("true"), ["type"] = "string" } }) },
        { "a header declared twice in different case", d => Parameters(d).Add(new JsonObject { ["in"] = "header", ["name"] = "typesafe-danger-zone-long-context", ["schema"] = new JsonObject { ["enum"] = new JsonArray("true"), ["type"] = "string" } }) },
        { "a header name that is not a token", d => Parameters(d)[1]!["name"] = "Long Context" },
        { "a reference to a schema the document does not have", d => d["components"]!["schemas"]!.AsObject().Remove("BoxCoordinates") },
        { "a string format", d => d.Property("ModelMetadata", "release_date")["format"] = "date" },
        { "an enum of numbers", d => d.Property("NoulAnswer", "noul")["enum"] = new JsonArray(0, 1) },
        { "an enum of strings with no type", d => d.Property("Asset", "format").Remove("type") },
        { "an enum that lists a value twice", d => d.Property("Asset", "format")["enum"] = new JsonArray("png", "png") },
        { "an empty enum", d => d.Property("Asset", "format")["enum"] = new JsonArray() },
        { "an enum with a bound beside it", d => d.Property("Asset", "format")["maxLength"] = 4 },
        { "an enum inside a list", d => d.Property("ValidationError", "loc")["items"] = new JsonObject { ["enum"] = new JsonArray("body"), ["type"] = "string" } },
        { "a required constant outside a union", d => d.Schema("Asset")["required"] = new JsonArray("type") },
        { "a form whose JSON is another schema", d => Form(d)["schema"]!["properties"]!["request"]!["contentSchema"]!["$ref"] = "#/components/schemas/Usage" },
        { "a form whose first part is not JSON", d => Form(d)["schema"]!["properties"]!["request"]!["contentMediaType"] = "text/plain" },
        { "a form with a second named part", d => Form(d)["schema"]!["properties"]!["metadata"] = new JsonObject { ["type"] = "string" } },
        { "a form that does not require its JSON", d => Form(d)["schema"]!.AsObject().Remove("required") },
        { "a form whose files are not images", d => Form(d)["schema"]!["additionalProperties"]!["contentMediaType"] = "application/pdf" },
        { "a form whose files are not strings", d => Form(d)["schema"]!["additionalProperties"]!["type"] = "object" },
        { "a form with no files", d => Form(d)["schema"]!.AsObject().Remove("additionalProperties") },
        { "a form with no encoding", d => Form(d).Remove("encoding") },
        { "a form whose encoding names another part", d => Form(d)["encoding"] = new JsonObject { ["metadata"] = new JsonObject { ["contentType"] = "application/json" } } },
        { "a form encoded as something other than JSON", d => Form(d)["encoding"]!["request"]!["contentType"] = "text/plain" },
        { "a request body in another media type", d => d["paths"]!["/v1/systemone"]!["post"]!["requestBody"]!["content"]!["application/x-www-form-urlencoded"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } } },
        { "a form with no JSON beside it", d => d["paths"]!["/v1/systemone"]!["post"]!["requestBody"]!["content"]!.AsObject().Remove("application/json") },
        { "a numeric bound", d => d.Property("NoulAnswer", "noul")["maximum"] = 1 },
        { "allOf", d => d.Schema("Usage")["allOf"] = new JsonArray() },
        { "an external reference", d => d.Property("SystemOneResponse", "usage")["$ref"] = "https://example.com/usage.json" },
        { "a oneOf without a discriminator", d => d.Schema("Answer").Remove("discriminator") },
        { "a mapping that differs from the branches", d => d["components"]!["schemas"]!["Answer"]!["discriminator"]!["mapping"]!.AsObject().Remove("noul") },
        { "a discriminator constant that disagrees", d => d.Property("NoulAnswer", "type")["const"] = "yesno" },
        { "an api key scheme", d => d["components"]!["securitySchemes"]!["HTTPBearer"]!["type"] = "apiKey" },
        { "OpenAPI 3.0", d => d["openapi"] = "3.0.3" },
        { "a non-JSON response", d => d["paths"]!["/v1/models"]!["get"]!["responses"]!["200"]!["content"]!.AsObject().Add("text/plain", new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } }) },
        { "a query parameter", d => d["paths"]!["/v1/models"]!["get"]!["parameters"] = new JsonArray(new JsonObject { ["in"] = "query", ["name"] = "limit", ["schema"] = new JsonObject { ["enum"] = new JsonArray("10"), ["type"] = "string" } }) },
        { "parameters that are not a list", d => d["paths"]!["/v1/models"]!["get"]!["parameters"] = new JsonObject() },
        { "a typed object inside an anyOf", d => d.Property("SystemOneRequest", "state")["anyOf"]![1]!["additionalProperties"] = new JsonObject { ["type"] = "string" } },
        { "a required property that is not declared", d => d.Schema("Usage")["required"]!.AsArray().Add("cached_tokens") },
        { "a free-form object with a minimum", d => d.Property("ValidationError", "ctx")["minProperties"] = 1 },
        { "a server other than the default base address", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://eu.api.typesafe.ai" }) },
        { "the default base address with a path", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai/v2" }) },
        { "a second server", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai" }, new JsonObject { ["url"] = "https://eu.api.typesafe.ai" }) },
        { "an empty server list", d => d["servers"] = new JsonArray() },
        { "a server with variables", d => d["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.typesafe.ai", ["variables"] = new JsonObject() }) },
    };

    /// <summary>The parameters of <c>POST /v1/systemone</c>: the images header, then the long-context one, both approved.</summary>
    private static JsonArray Parameters(JsonObject document) => document["paths"]!["/v1/systemone"]!["post"]!["parameters"]!.AsArray();

    /// <summary>The <c>multipart/form-data</c> form of <c>POST /v1/systemone</c>, which carries images.</summary>
    private static JsonObject Form(JsonObject document) => document["paths"]!["/v1/systemone"]!["post"]!["requestBody"]!["content"]!["multipart/form-data"]!.AsObject();

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
