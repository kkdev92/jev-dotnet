using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Tests;

/// <summary>
/// What extracting the contract removes, and — the part that matters more — what it keeps.
/// </summary>
public sealed class ContractExtractorTests
{
    private static JsonNode Extract(string json) => JsonNode.Parse(ContractExtractor.Extract(Encoding.UTF8.GetBytes(json)))!;

    [Fact]
    public void AnnotationsAreRemovedWhereverTheyAreKeywords()
    {
        var extracted = Extract("""
            {
              "openapi": "3.1.0",
              "info": { "title": "TypeSafe", "description": "Prose.", "version": "0.2.0" },
              "paths": {
                "/v1/things": {
                  "summary": "Prose.",
                  "post": {
                    "summary": "Prose.",
                    "description": "Prose.",
                    "responses": { "200": { "description": "Prose.", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } } } }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Thing": {
                    "title": "Thing",
                    "description": "Prose.",
                    "$comment": "Prose.",
                    "type": "object",
                    "properties": {
                      "size": { "title": "Size", "description": "Prose.", "examples": [1, 2], "example": 3, "type": "integer" },
                      "kind": { "anyOf": [ { "type": "string", "title": "Prose" }, { "type": "null", "description": "Prose." } ] }
                    }
                  }
                }
              }
            }
            """);

        var expected = JsonNode.Parse("""
            {
              "openapi": "3.1.0",
              "info": { "title": "TypeSafe", "version": "0.2.0" },
              "paths": {
                "/v1/things": {
                  "post": {
                    "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } } } }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Thing": {
                    "type": "object",
                    "properties": {
                      "size": { "type": "integer" },
                      "kind": { "anyOf": [ { "type": "string" }, { "type": "null" } ] }
                    }
                  }
                }
              }
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, extracted), extracted.ToJsonString());
    }

    /// <summary>
    /// A name is not an annotation, whatever it is spelled like.
    /// </summary>
    /// <remarks>
    /// The case that made this worth writing is real: <c>ModelMetadata</c> has a property called
    /// <c>description</c>. Removing members by name alone would delete a field of the contract and
    /// the generator would never know it had existed.
    /// </remarks>
    [Fact]
    public void NamesThatLookLikeAnnotationsAreKept()
    {
        var extracted = Extract("""
            {
              "info": { "title": "TypeSafe", "version": "1" },
              "paths": { "/description": { "get": { "security": [ { "title": [] } ], "responses": {} } } },
              "components": {
                "schemas": {
                  "title": { "type": "string" },
                  "Model": {
                    "type": "object",
                    "properties": {
                      "description": { "type": "string" },
                      "title": { "type": "string" },
                      "examples": { "type": "string", "enum": ["description", "title"], "default": { "description": "data" } }
                    },
                    "required": ["description", "title"]
                  },
                  "Answer": { "oneOf": [], "discriminator": { "propertyName": "type", "mapping": { "summary": "#/components/schemas/title" } } }
                }
              }
            }
            """);

        var schemas = extracted["components"]!["schemas"]!;
        var properties = schemas["Model"]!["properties"]!.AsObject();

        Assert.NotNull(schemas["title"]);
        Assert.Equal(["description", "examples", "title"], properties.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["description", "title"], schemas["Model"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("data", properties["examples"]!["default"]!["description"]!.GetValue<string>());
        Assert.Equal(["description", "title"], properties["examples"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotNull(schemas["Answer"]!["discriminator"]!["mapping"]!["summary"]);
        Assert.NotNull(extracted["paths"]!["/description"]!["get"]!["security"]![0]!["title"]);
    }

    [Fact]
    public void NumbersKeepTheirSpelling()
    {
        var extracted = ContractExtractor.Extract("""{"a":{"minimum":1.0,"maximum":1e3,"multipleOf":0.50}}"""u8);
        var text = Encoding.UTF8.GetString(extracted);

        Assert.Contains("1.0", text, StringComparison.Ordinal);
        Assert.Contains("1e3", text, StringComparison.Ordinal);
        Assert.Contains("0.50", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommittedContractIsAFixedPoint()
    {
        var committed = SpecFixture.Committed().CanonicalBytes;

        Assert.Equal(committed, ContractExtractor.Extract(committed));
    }

    /// <summary>
    /// Annotating the committed contract everywhere TypeSafe's document puts annotations, then
    /// extracting, gives back exactly the committed bytes.
    /// </summary>
    /// <remarks>
    /// This is the round trip that says the extractor removes what the upstream document adds and
    /// nothing else, without committing the upstream document to say it.
    /// </remarks>
    [Fact]
    public void AnnotatingAndExtractingIsTheIdentity()
    {
        var document = SpecFixture.Document();

        document["info"]!["description"] = "Prose.";

        foreach (var (_, item) in document["paths"]!.AsObject())
        {
            foreach (var (_, operation) in item!.AsObject())
            {
                operation!["summary"] = "Prose.";
                operation["description"] = "Prose.";

                foreach (var (_, response) in operation["responses"]!.AsObject())
                {
                    response!["description"] = "Prose.";
                }
            }
        }

        foreach (var (_, schema) in document["components"]!["schemas"]!.AsObject())
        {
            schema!["title"] = "Prose";
            schema["description"] = "Prose.";

            if (schema["properties"] is JsonObject properties)
            {
                foreach (var (_, property) in properties)
                {
                    property!["title"] = "Prose";
                    property["description"] = "Prose.";
                    property["examples"] = new JsonArray("an example");
                }
            }
        }

        var extracted = ContractExtractor.Extract(JsonSerializer.SerializeToUtf8Bytes(document));

        Assert.Equal(SpecFixture.Committed().CanonicalBytes, extracted);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' })]
    [InlineData(new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xC3, (byte)'"', (byte)'}' })]
    public void WhatTheCanonicalizerRefusesTheExtractorRefuses(byte[] document)
        => Assert.Throws<InvalidDataException>(() => ContractExtractor.Extract(document));

    [Fact]
    public void ARepeatedKeyIsRefused()
        => Assert.Throws<InvalidDataException>(() => ContractExtractor.Extract("""{"a":1,"a":2}"""u8));
}
