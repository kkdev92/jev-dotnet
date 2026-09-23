using System.Text.Json;
using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.Normalization;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.OpenApi;

/// <summary>
/// Reads the subset of OpenAPI 3.1 this API uses into the intermediate model, and refuses the rest.
/// </summary>
/// <remarks>
/// <para>
/// This is not a general OpenAPI generator and does not try to be one. It understands the
/// keywords the TypeSafe document uses today — objects with properties, maps, arrays,
/// <c>anyOf</c> over value kinds, a nullable reference, a discriminated <c>oneOf</c>,
/// <c>minItems</c> and <c>minProperties</c> — and fails closed on everything else.
/// </para>
/// <para>
/// Failing closed is the point. If a later snapshot adds <c>maxItems: 10</c> to the score criteria,
/// the right outcome is a generator that stops and a maintainer who decides what the SDK does with
/// it, not a generator that drops the keyword and reports success. Annotations that cannot change
/// the shape of the wire — titles, descriptions, examples — are read past and never emitted.
/// </para>
/// </remarks>
internal static class OpenApiParser
{
    private const string SchemaPrefix = "#/components/schemas/";

    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "title", "description", "examples", "$comment",
    };

    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    public static ApiContract Parse(SpecSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.CanonicalBytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;

        Expect(root, "#", allowed: ["openapi", "info", "paths", "components"]);

        var openApi = String(root, "openapi", "#");

        if (!openApi.StartsWith("3.1.", StringComparison.Ordinal))
        {
            throw Unsupported("#/openapi", $"OpenAPI {openApi}. This generator reads 3.1 only: 3.0 expresses null with 'nullable', which it does not interpret.");
        }

        var info = root.GetProperty("info");
        Expect(info, "#/info", allowed: ["title", "description", "version"]);

        var components = root.GetProperty("components");
        Expect(components, "#/components", allowed: ["schemas", "securitySchemes"]);

        var securitySchemes = ParseSecuritySchemes(components);
        var operations = ParseOperations(root.GetProperty("paths"), securitySchemes);
        var schemas = ParseSchemas(components.GetProperty("schemas"), snapshot.NamingOverrides);

        return new ApiContract(
            snapshot.Contract,
            openApi,
            String(info, "title", "#/info"),
            String(info, "version", "#/info"),
            snapshot.Provenance.Snapshot.Sha256,
            operations,
            schemas,
            snapshot.Semantics.Constants);
    }

    private static HashSet<string> ParseSecuritySchemes(JsonElement components)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (!components.TryGetProperty("securitySchemes", out var schemes))
        {
            return names;
        }

        foreach (var scheme in schemes.EnumerateObject())
        {
            var location = $"#/components/securitySchemes/{scheme.Name}";
            Expect(scheme.Value, location, allowed: ["type", "scheme", "bearerFormat", "description"]);

            // The runtime attaches 'Authorization: Bearer <key>' by hand. A scheme of any other shape
            // would mean the service authenticates differently from the code that ships, so it stops
            // generation rather than generating something the runtime does not do.
            if (String(scheme.Value, "type", location) != "http"
                || !string.Equals(String(scheme.Value, "scheme", location), "bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported(location, "a security scheme other than HTTP bearer. The runtime only sends 'Authorization: Bearer'.");
            }

            names.Add(scheme.Name);
        }

        return names;
    }

    private static List<OperationDef> ParseOperations(JsonElement paths, HashSet<string> securitySchemes)
    {
        var operations = new List<OperationDef>();

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                var location = $"#/paths/{Escape(path.Name)}/{method.Name}";

                if (!HttpMethods.Contains(method.Name))
                {
                    throw Unsupported(location, $"'{method.Name}' under a path. Path-level parameters and servers are not interpreted.");
                }

                var operation = method.Value;
                Expect(operation, location, allowed: ["summary", "description", "operationId", "requestBody", "responses", "security", "tags"]);

                string? requestSchema = null;
                var requestRequired = false;

                if (operation.TryGetProperty("requestBody", out var requestBody))
                {
                    Expect(requestBody, location + "/requestBody", allowed: ["content", "required", "description"]);
                    requestSchema = JsonContentSchema(requestBody, location + "/requestBody");
                    requestRequired = requestBody.TryGetProperty("required", out var required) && required.GetBoolean();
                }

                var responses = new List<ResponseDef>();

                foreach (var response in operation.GetProperty("responses").EnumerateObject())
                {
                    var responseLocation = $"{location}/responses/{response.Name}";
                    Expect(response.Value, responseLocation, allowed: ["description", "content"]);

                    responses.Add(new ResponseDef(
                        response.Name,
                        response.Value.TryGetProperty("content", out _) ? JsonContentSchema(response.Value, responseLocation) : null));
                }

                var security = new List<string>();

                if (operation.TryGetProperty("security", out var requirements))
                {
                    foreach (var requirement in requirements.EnumerateArray())
                    {
                        foreach (var scheme in requirement.EnumerateObject())
                        {
                            if (!securitySchemes.Contains(scheme.Name))
                            {
                                throw Unsupported(location + "/security", $"a requirement for undeclared scheme '{scheme.Name}'.");
                            }

                            if (scheme.Value.GetArrayLength() != 0)
                            {
                                throw Unsupported(location + "/security", $"scopes on '{scheme.Name}'. Bearer authentication has none.");
                            }

                            security.Add(scheme.Name);
                        }
                    }
                }

                operations.Add(new OperationDef(
                    new OperationKey(method.Name.ToUpperInvariant(), path.Name),
                    operation.TryGetProperty("operationId", out var id) ? id.GetString() : null,
                    requestSchema,
                    requestRequired,
                    responses,
                    security));
            }
        }

        return operations;
    }

    /// <summary>The schema of an <c>application/json</c> body, which must be a reference.</summary>
    private static string JsonContentSchema(JsonElement owner, string location)
    {
        var content = owner.GetProperty("content");
        var mediaTypes = content.EnumerateObject().Select(p => p.Name).ToList();

        if (mediaTypes is not ["application/json"])
        {
            throw Unsupported(location + "/content", $"media types [{string.Join(", ", mediaTypes)}]. Only application/json is interpreted.");
        }

        var media = content.GetProperty("application/json");
        Expect(media, location + "/content/application~1json", allowed: ["schema"]);

        var schema = media.GetProperty("schema");
        Expect(schema, location + "/content/application~1json/schema", allowed: ["$ref"]);

        return SchemaName(String(schema, "$ref", location), location);
    }

    private static List<SchemaDef> ParseSchemas(JsonElement schemas, NamingOverrides overrides)
    {
        var result = new List<SchemaDef>();
        var unions = new Dictionary<string, (string Union, string Tag)>(StringComparer.Ordinal);

        // Unions first: an object's base class and discriminator value come from the union that
        // lists it, and the canonical document orders schemas by name rather than by dependency.
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("oneOf", out _))
            {
                continue;
            }

            var union = ParseUnion(schema.Name, schema.Value, CSharpTypeName(schema.Name, overrides));
            result.Add(union);

            foreach (var member in union.Members)
            {
                if (!unions.TryAdd(member.SchemaWireName, (schema.Name, member.Tag)))
                {
                    throw Unsupported($"#/components/schemas/{schema.Name}", $"'{member.SchemaWireName}' as a member of two unions. A generated class has one base.");
                }
            }
        }

        foreach (var schema in schemas.EnumerateObject())
        {
            if (schema.Value.TryGetProperty("oneOf", out _))
            {
                continue;
            }

            unions.TryGetValue(schema.Name, out var membership);

            result.Add(ParseObject(
                schema.Name,
                schema.Value,
                CSharpTypeName(schema.Name, overrides),
                membership.Union,
                membership.Tag));
        }

        foreach (var name in overrides.Schemas.Keys)
        {
            if (!result.Any(s => string.Equals(s.WireName, name, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"{NamingOverrides.FileName} renames '{name}', which the document does not declare. Remove the stale override.");
            }
        }

        return result;
    }

    private static UnionSchemaDef ParseUnion(string name, JsonElement schema, string csharpName)
    {
        var location = $"#/components/schemas/{name}";
        Expect(schema, location, allowed: ["oneOf", "discriminator"]);

        if (!schema.TryGetProperty("discriminator", out var discriminator))
        {
            throw Unsupported(location, "a oneOf without a discriminator. Telling members apart by trial parsing is not something the runtime should do.");
        }

        Expect(discriminator, location + "/discriminator", allowed: ["propertyName", "mapping"]);

        var propertyName = String(discriminator, "propertyName", location + "/discriminator");
        var branches = schema.GetProperty("oneOf").EnumerateArray()
            .Select((branch, i) =>
            {
                Expect(branch, $"{location}/oneOf/{i}", allowed: ["$ref"]);
                return SchemaName(String(branch, "$ref", location), location);
            })
            .ToList();

        var members = discriminator.GetProperty("mapping").EnumerateObject()
            .Select(p => new UnionMember(p.Name, SchemaName(p.Value.GetString() ?? string.Empty, location + "/discriminator/mapping")))
            .ToList();

        var mapped = members.Select(m => m.SchemaWireName).Order(StringComparer.Ordinal).ToList();

        if (!mapped.SequenceEqual(branches.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Unsupported(location, "a discriminator mapping that does not name exactly the oneOf branches.");
        }

        return new UnionSchemaDef(name, csharpName, propertyName, members);
    }

    private static ObjectSchemaDef ParseObject(string name, JsonElement schema, string csharpName, string? union, string? tag)
    {
        var location = $"#/components/schemas/{name}";
        Expect(schema, location, allowed: ["type", "properties", "required"]);

        if (String(schema, "type", location) != "object" || !schema.TryGetProperty("properties", out var properties))
        {
            throw Unsupported(location, "a named schema that is not an object with properties or a discriminated oneOf.");
        }

        var required = schema.TryGetProperty("required", out var requiredArray)
            ? requiredArray.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

        var result = new List<PropertyDef>();
        var csharpNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in properties.EnumerateObject())
        {
            var propertyLocation = $"{location}/properties/{Escape(property.Name)}";
            var type = ParseType(property.Value, propertyLocation);
            var propertyName = Naming.ToPascalCase(property.Name);

            if (!csharpNames.Add(propertyName) || propertyName == csharpName)
            {
                throw new InvalidDataException($"{propertyLocation}: '{Naming.Printable(property.Name)}' becomes '{propertyName}', which is already taken in '{csharpName}'.");
            }

            result.Add(new PropertyDef(property.Name, propertyName, type, required.Contains(property.Name)));
        }

        foreach (var name2 in required)
        {
            if (!result.Any(p => p.WireName == name2))
            {
                throw Unsupported(location + "/required", $"'{Naming.Printable(name2)}' is required but not declared.");
            }
        }

        if (union is not null)
        {
            var discriminatorProperty = result.FirstOrDefault(p => p.Type is ConstRef);

            if (discriminatorProperty?.Type is not ConstRef constant || constant.Value != tag || !discriminatorProperty.Required)
            {
                throw Unsupported(location, $"a member of '{union}' whose discriminator is not a required constant '{tag}'.");
            }
        }
        else if (result.Any(p => p.Type is ConstRef))
        {
            throw Unsupported(location, "a constant property outside a discriminated union.");
        }

        return new ObjectSchemaDef(name, csharpName, result, union, tag);
    }

    /// <summary>The type of a property, an item or a map value.</summary>
    internal static TypeRef ParseType(JsonElement schema, string location)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw Unsupported(location, $"a schema of kind {schema.ValueKind}.");
        }

        var keys = schema.EnumerateObject().Select(p => p.Name).Where(k => !Annotations.Contains(k)).ToHashSet(StringComparer.Ordinal);

        if (keys.Count == 0)
        {
            // An empty schema accepts anything.
            return new JsonValueRef(JsonKinds.Any);
        }

        if (keys.Contains("$ref"))
        {
            Only(keys, location, "$ref");
            return new SchemaRef(SchemaName(String(schema, "$ref", location), location));
        }

        if (keys.Contains("anyOf"))
        {
            Only(keys, location, "anyOf");
            return ParseAnyOf(schema.GetProperty("anyOf"), location);
        }

        if (keys.Contains("const"))
        {
            Only(keys, location, "const", "type");

            if (String(schema, "type", location) != "string")
            {
                throw Unsupported(location, "a non-string constant.");
            }

            return new ConstRef(String(schema, "const", location));
        }

        switch (String(schema, "type", location))
        {
            case "string":
                Only(keys, location, "type");
                return new PrimitiveRef(PrimitiveKind.String);

            case "number":
                Only(keys, location, "type");
                return new PrimitiveRef(PrimitiveKind.Number);

            case "integer":
                Only(keys, location, "type");
                return new PrimitiveRef(PrimitiveKind.Integer);

            case "object":
                {
                    Only(keys, location, "type", "additionalProperties", "minProperties");

                    if (!schema.TryGetProperty("additionalProperties", out var additional)
                        || additional.ValueKind == JsonValueKind.True
                        || (additional.ValueKind == JsonValueKind.Object && !additional.EnumerateObject().Any()))
                    {
                        if (keys.Contains("minProperties"))
                        {
                            throw Unsupported(location, "minProperties on a free-form object.");
                        }

                        return new JsonValueRef(JsonKinds.Object);
                    }

                    if (additional.ValueKind != JsonValueKind.Object)
                    {
                        throw Unsupported(location + "/additionalProperties", $"additionalProperties of kind {additional.ValueKind}.");
                    }

                    return new MapRef(ParseType(additional, location + "/additionalProperties"), OptionalCount(schema, "minProperties", location));
                }

            case "array":
                {
                    Only(keys, location, "type", "items", "minItems");

                    var items = schema.TryGetProperty("items", out var itemSchema)
                        ? ParseType(itemSchema, location + "/items")
                        : new JsonValueRef(JsonKinds.Any);

                    return new ArrayRef(items, OptionalCount(schema, "minItems", location));
                }

            default:
                throw Unsupported(location, $"type '{String(schema, "type", location)}'.");
        }
    }

    /// <summary>
    /// An <c>anyOf</c> is either a set of value kinds or a nullable reference. Nothing else is interpreted.
    /// </summary>
    private static TypeRef ParseAnyOf(JsonElement branches, string location)
    {
        var list = branches.EnumerateArray().ToList();

        if (list.Count == 2
            && list.Count(IsNullBranch) == 1
            && list.SingleOrDefault(b => !IsNullBranch(b)) is { ValueKind: JsonValueKind.Object } other
            && other.TryGetProperty("$ref", out _))
        {
            var reference = ParseType(other, location + "/anyOf");

            return reference is SchemaRef schemaRef
                ? new NullableRef(schemaRef)
                : throw Unsupported(location, "a nullable reference with extra constraints.");
        }

        var kinds = JsonKinds.None;

        for (var i = 0; i < list.Count; i++)
        {
            var kind = BranchKind(list[i], $"{location}/anyOf/{i}");

            if ((kinds & kind) != 0)
            {
                throw Unsupported(location, "an anyOf that lists the same kind twice.");
            }

            kinds |= kind;
        }

        return new JsonValueRef(kinds);
    }

    private static bool IsNullBranch(JsonElement branch)
        => branch.ValueKind == JsonValueKind.Object
            && branch.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "null";

    /// <summary>The value kind an <c>anyOf</c> branch admits, when it is nothing more than a kind.</summary>
    private static JsonKinds BranchKind(JsonElement branch, string location)
    {
        var type = ParseTypeName(branch, location);
        var keys = branch.EnumerateObject().Select(p => p.Name).Where(k => !Annotations.Contains(k)).ToHashSet(StringComparer.Ordinal);

        switch (type)
        {
            case "string":
                Only(keys, location, "type");
                return JsonKinds.String;
            case "number":
                Only(keys, location, "type");
                return JsonKinds.Number;
            case "integer":
                Only(keys, location, "type");
                return JsonKinds.Integer;
            case "boolean":
                Only(keys, location, "type");
                return JsonKinds.Boolean;
            case "null":
                Only(keys, location, "type");
                return JsonKinds.Null;
            case "object":
                Only(keys, location, "type", "additionalProperties");

                if (branch.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.True)
                {
                    throw Unsupported(location, "a typed object inside an anyOf. Only a free-form object is interpreted there.");
                }

                return JsonKinds.Object;
            case "array":
                Only(keys, location, "type", "items");

                if (branch.TryGetProperty("items", out var items) && (items.ValueKind != JsonValueKind.Object || items.EnumerateObject().Any()))
                {
                    throw Unsupported(location, "a typed array inside an anyOf. Only a free-form array is interpreted there.");
                }

                return JsonKinds.Array;
            default:
                throw Unsupported(location, $"an anyOf branch of type '{type}'.");
        }
    }

    private static string ParseTypeName(JsonElement branch, string location)
        => branch.ValueKind == JsonValueKind.Object && branch.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()!
            : throw Unsupported(location, "an anyOf branch without a single type.");

    private static int? OptionalCount(JsonElement schema, string keyword, string location)
    {
        if (!schema.TryGetProperty(keyword, out var value))
        {
            return null;
        }

        return value.TryGetInt32(out var count) && count >= 0
            ? count
            : throw Unsupported(location + "/" + keyword, $"a {keyword} that is not a non-negative integer.");
    }

    private static string SchemaName(string reference, string location)
    {
        if (!reference.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            // External references would have to be fetched during generation, which is never allowed.
            throw Unsupported(location, $"the reference '{Naming.Printable(reference)}'. Only local #/components/schemas references are resolved.");
        }

        var name = reference[SchemaPrefix.Length..];

        return Naming.IsTypeName(name)
            ? name
            : throw Unsupported(location, $"the schema name '{Naming.Printable(name)}', which is not a plain identifier.");
    }

    private static string CSharpTypeName(string wireName, NamingOverrides overrides)
    {
        var name = overrides.Schemas.TryGetValue(wireName, out var renamed) ? renamed : wireName;

        return Naming.IsTypeName(name)
            ? name
            : throw new InvalidDataException($"'{Naming.Printable(name)}' is not usable as a type name.");
    }

    private static void Expect(JsonElement element, string location, IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Unsupported(location, $"a value of kind {element.ValueKind} where an object was expected.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) && !Annotations.Contains(property.Name))
            {
                throw Unsupported(location, $"the keyword '{Naming.Printable(property.Name)}'.");
            }
        }
    }

    private static void Only(HashSet<string> keys, string location, params string[] allowed)
    {
        foreach (var key in keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw Unsupported(location, $"the keyword '{Naming.Printable(key)}' alongside '{allowed[0]}'.");
            }
        }
    }

    private static string String(JsonElement element, string name, string location)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Unsupported(location, $"a missing or non-string '{name}'.");

    /// <summary>JSON pointer escaping for a location in an error message.</summary>
    private static string Escape(string segment) => Naming.Printable(segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));

    private static UnsupportedSpecificationException Unsupported(string location, string what)
        => new($"{location}: unsupported {what}");
}

/// <summary>The document uses something this generator does not interpret. Generation stops.</summary>
internal sealed class UnsupportedSpecificationException(string message) : Exception(message);
