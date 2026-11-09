using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <c>anyOf</c> over value kinds, a nullable reference, a discriminated <c>oneOf</c>, a string
/// <c>enum</c>, an optional constant, <c>minItems</c> and <c>minProperties</c>, optional headers
/// that take one fixed value, the <c>multipart/form-data</c> form the runtime sends images in, the
/// one server the runtime sends to — and fails closed on everything else.
/// </para>
/// <para>
/// Failing closed is the point. If a snapshot adds <c>maxItems: 10</c> to the score criteria, the
/// right outcome is a generator that stops and a maintainer who decides what the SDK does with it,
/// not a generator that drops the keyword and reports success. Once decided, the decision is
/// written down where the generator can hold the document to it: a maximum is read where
/// <c>semantics.json</c> pins its value, and a feature the SDK does not implement is named, as it
/// was, in <c>public-surface.json</c>. Annotations that cannot change the shape of the wire —
/// titles, descriptions, examples — are read past and never emitted.
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
        var bytes = WithoutOmittedParts(snapshot.CanonicalBytes, snapshot.PublicSurface.Omitted);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;

        Expect(root, "#", allowed: ["openapi", "info", "servers", "paths", "components"]);

        var openApi = String(root, "openapi", "#");

        if (!openApi.StartsWith("3.1.", StringComparison.Ordinal))
        {
            throw Unsupported("#/openapi", $"OpenAPI {openApi}. This generator reads 3.1 only: 3.0 expresses null with 'nullable', which it does not interpret.");
        }

        var info = root.GetProperty("info");
        Expect(info, "#/info", allowed: ["title", "description", "version"]);

        ParseServers(root, snapshot.Semantics.Constants);

        var components = root.GetProperty("components");
        Expect(components, "#/components", allowed: ["schemas", "securitySchemes"]);

        var securitySchemes = ParseSecuritySchemes(components);
        var operations = ParseOperations(root.GetProperty("paths"), securitySchemes);
        var schemas = ParseSchemas(components.GetProperty("schemas"), snapshot.NamingOverrides, PinnedMaxima(snapshot.Semantics));
        ExpectReferencesResolve(operations, schemas);

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

    /// <summary>
    /// The document without the parts <c>public-surface.json</c> leaves out. Each must be there
    /// exactly as recorded. Every pointer names a part of the document as committed, so an array
    /// element is named by its index before anything is removed. Leaving out a discriminator
    /// mapping entry leaves out its <c>oneOf</c> branch with it, so the union is read as one member
    /// shorter.
    /// </summary>
    private static byte[] WithoutOmittedParts(byte[] canonical, IReadOnlyList<OmittedPart> omitted)
    {
        if (omitted.Count == 0)
        {
            return canonical;
        }

        var root = JsonNode.Parse(canonical)!.AsObject();
        var resolved = new List<(OmittedPart Part, string[] Segments, JsonNode Parent, JsonNode? Value)>();
        var pointers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in omitted)
        {
            if (!pointers.Add(part.Pointer))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} omits {part.Pointer} twice.");
            }

            var segments = PointerSegments(part.Pointer);
            JsonNode? parent = root;

            foreach (var segment in segments[..^1])
            {
                parent = TryChild(parent, segment, out var next) ? next : null;
            }

            if (parent is null || !TryChild(parent, segments[^1], out var value))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} omits {part.Pointer}, which the document does not have. Remove the stale entry.");
            }

            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value), part.Value))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} omits {part.Pointer} as it was when that was decided, and the document now has something else there. Decide again.");
            }

            resolved.Add((part, segments, parent, value));
        }

        // Removing an element moves the ones after it, so elements go last first, and before a
        // branch is looked up by what it refers to.
        foreach (var (_, segments, parent, _) in resolved.Where(r => r.Parent is JsonArray).OrderByDescending(r => ArrayIndex(r.Segments[^1])))
        {
            parent.AsArray().RemoveAt(ArrayIndex(segments[^1])!.Value);
        }

        foreach (var (part, segments, parent, value) in resolved.Where(r => r.Parent is JsonObject))
        {
            parent.AsObject().Remove(segments[^1]);

            if (segments is ["components", "schemas", var union, "discriminator", "mapping", _])
            {
                var branches = root["components"]!["schemas"]![union]!["oneOf"]!.AsArray();
                var branch = branches.SingleOrDefault(b => b?["$ref"]?.GetValue<string>() == value?.GetValue<string>())
                    ?? throw new InvalidDataException($"{PublicSurface.FileName} omits {part.Pointer}, and '{union}' has no oneOf branch for it.");

                branches.Remove(branch);
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    /// <summary>The member or element that one pointer segment names, when there is one.</summary>
    private static bool TryChild(JsonNode? node, string segment, out JsonNode? child)
    {
        switch (node)
        {
            case JsonObject members:
                return members.TryGetPropertyValue(segment, out child);

            case JsonArray elements when ArrayIndex(segment) is { } index && index < elements.Count:
                child = elements[index];
                return true;

            default:
                child = null;
                return false;
        }
    }

    /// <summary>A pointer segment read as an array index: digits, with no leading zero.</summary>
    private static int? ArrayIndex(string segment)
        => segment.Length > 0
            && segment.All(char.IsAsciiDigit)
            && (segment.Length == 1 || segment[0] != '0')
            && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index
                : null;

    /// <summary>The segments of an RFC 6901 pointer that names a member or an element.</summary>
    private static string[] PointerSegments(string pointer)
        => pointer.Length > 1 && pointer[0] == '/'
            ? pointer[1..].Split('/').Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray()
            : throw new InvalidDataException($"'{pointer}' is not a JSON pointer to a member or an element.");

    /// <summary>Pointers to a <c>maxItems</c> or <c>maxProperties</c> whose value <c>semantics.json</c> pins with an <c>equals</c> check.</summary>
    private static HashSet<string> PinnedMaxima(Semantics semantics)
        => semantics.Conflicts
            .SelectMany(c => c.Checks)
            .Where(c => c.Operator == "equals"
                && (c.Pointer.EndsWith("/maxItems", StringComparison.Ordinal) || c.Pointer.EndsWith("/maxProperties", StringComparison.Ordinal)))
            .Select(c => c.Pointer)
            .ToHashSet(StringComparer.Ordinal);

    private static void ParseServers(JsonElement root, IReadOnlyList<SemanticConstant> constants)
    {
        if (!root.TryGetProperty("servers", out var servers))
        {
            return;
        }

        // The runtime sends every request to the defaultBaseAddress constant, not to whatever the
        // document lists. A document naming exactly that origin agrees with the code that ships; any
        // other server, a second one or a templated one would mean requests belong somewhere the
        // runtime does not send them, so generation stops until the constant is revisited.
        var expected = constants.SingleOrDefault(c => c.Name == "defaultBaseAddress")?.StringValue
            ?? throw new InvalidDataException("semantics.json has no string constant 'defaultBaseAddress' to compare #/servers with.");

        if (servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() != 1)
        {
            throw Unsupported("#/servers", $"a server list that is not exactly one entry. The runtime sends every request to {expected}.");
        }

        var server = servers[0];
        Expect(server, "#/servers/0", allowed: ["url"]);

        var url = String(server, "url", "#/servers/0");

        if (!string.Equals(url, expected, StringComparison.Ordinal))
        {
            throw Unsupported("#/servers/0/url", $"the server '{Naming.Printable(url)}'. The runtime sends every request to {expected} (defaultBaseAddress in semantics.json).");
        }
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
                Expect(operation, location, allowed: ["summary", "description", "operationId", "parameters", "requestBody", "responses", "security", "tags"]);

                var headers = operation.TryGetProperty("parameters", out var parameters)
                    ? ParseHeaders(parameters, location + "/parameters")
                    : [];

                string? requestSchema = null;
                FormDef? form = null;
                var requestRequired = false;

                if (operation.TryGetProperty("requestBody", out var requestBody))
                {
                    Expect(requestBody, location + "/requestBody", allowed: ["content", "required", "description"]);
                    (requestSchema, form) = RequestContent(requestBody, location + "/requestBody");
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
                    headers,
                    responses,
                    security,
                    form));
            }
        }

        return operations;
    }

    /// <summary>
    /// An operation's parameters, each of which must be an optional header with one allowed value:
    /// a switch the caller turns on by sending it.
    /// </summary>
    /// <remarks>
    /// Query, path and cookie parameters, a required header, and a header that carries a value of
    /// the caller's choosing would each need the runtime to send something it does not, so each
    /// stops generation.
    /// </remarks>
    private static List<HeaderDef> ParseHeaders(JsonElement parameters, string location)
    {
        if (parameters.ValueKind != JsonValueKind.Array)
        {
            throw Unsupported(location, $"parameters of kind {parameters.ValueKind}.");
        }

        var headers = new List<HeaderDef>();
        var index = 0;

        foreach (var parameter in parameters.EnumerateArray())
        {
            var parameterLocation = $"{location}/{index++}";
            Expect(parameter, parameterLocation, allowed: ["in", "name", "required", "schema"]);

            var where = String(parameter, "in", parameterLocation);

            if (where != "header")
            {
                throw Unsupported(parameterLocation, $"a parameter in '{Naming.Printable(where)}'. Only optional headers are interpreted.");
            }

            var name = String(parameter, "name", parameterLocation);

            if (!IsToken(name))
            {
                throw Unsupported(parameterLocation, $"the header name '{Naming.Printable(name)}', which is not an HTTP token.");
            }

            if (parameter.TryGetProperty("required", out var required) && required.ValueKind != JsonValueKind.False)
            {
                throw Unsupported(parameterLocation, $"a required '{name}' header. A header is sent only when the caller turns on what it switches.");
            }

            if (!parameter.TryGetProperty("schema", out var schema))
            {
                throw Unsupported(parameterLocation, $"a '{name}' header without a schema.");
            }

            Expect(schema, parameterLocation + "/schema", allowed: ["type", "enum"]);

            if (String(schema, "type", parameterLocation + "/schema") != "string"
                || !schema.TryGetProperty("enum", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() != 1
                || values[0].ValueKind != JsonValueKind.String
                || !IsToken(values[0].GetString()!))
            {
                throw Unsupported(parameterLocation + "/schema", $"a '{name}' header that takes something other than one fixed value.");
            }

            if (headers.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw Unsupported(parameterLocation, $"a second '{name}' header. Header names are case-insensitive.");
            }

            headers.Add(new HeaderDef(name, values[0].GetString()!));
        }

        return headers;
    }

    /// <summary>An RFC 9110 token, the shape of a header name and of the values this parser accepts for one.</summary>
    private static bool IsToken(string text)
        => text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));

    /// <summary>The schema of an <c>application/json</c> body, which must be a reference.</summary>
    private static string JsonContentSchema(JsonElement owner, string location)
    {
        var content = owner.GetProperty("content");
        var mediaTypes = content.EnumerateObject().Select(p => p.Name).ToList();

        if (mediaTypes is not ["application/json"])
        {
            throw Unsupported(location + "/content", $"media types [{string.Join(", ", mediaTypes)}]. Only application/json is interpreted.");
        }

        return JsonMediaSchema(content.GetProperty("application/json"), location);
    }

    /// <summary>
    /// A request body: an <c>application/json</c> body, and optionally the same body sent as a
    /// <c>multipart/form-data</c> form with files beside it.
    /// </summary>
    private static (string Schema, FormDef? Form) RequestContent(JsonElement requestBody, string location)
    {
        var content = requestBody.GetProperty("content");
        var mediaTypes = content.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        if (mediaTypes is not (["application/json"] or ["application/json", "multipart/form-data"]))
        {
            throw Unsupported(location + "/content", $"media types [{string.Join(", ", mediaTypes)}]. Only application/json is interpreted, alone or beside a multipart/form-data form of the same body.");
        }

        var schema = JsonMediaSchema(content.GetProperty("application/json"), location);

        return content.TryGetProperty("multipart/form-data", out var form)
            ? (schema, ParseForm(form, schema, location + "/content/multipart~1form-data"))
            : (schema, null);
    }

    private static string JsonMediaSchema(JsonElement media, string location)
    {
        Expect(media, location + "/content/application~1json", allowed: ["schema"]);

        if (!media.TryGetProperty("schema", out var schema))
        {
            throw Unsupported(location + "/content/application~1json", "a JSON body without a schema.");
        }

        Expect(schema, location + "/content/application~1json/schema", allowed: ["$ref"]);

        return SchemaName(String(schema, "$ref", location), location);
    }

    /// <summary>
    /// A <c>multipart/form-data</c> form, in the one shape this generator reads: the JSON body in a
    /// part of its own, encoded as <c>application/json</c> and holding the schema of the
    /// <c>application/json</c> request, and files of one media type in every other part.
    /// </summary>
    /// <remarks>
    /// The runtime sends a form only to carry images, writing the JSON part under the name read
    /// here and each image as a file part typed <c>image/png</c> or <c>image/jpeg</c>. A form with
    /// another named part, a JSON part of another schema, or files of another media type would
    /// need the runtime to send something it does not, so each stops generation.
    /// </remarks>
    private static FormDef ParseForm(JsonElement media, string jsonSchema, string location)
    {
        Expect(media, location, allowed: ["schema", "encoding"]);

        if (!media.TryGetProperty("schema", out var schema))
        {
            throw Unsupported(location, "a form without a schema.");
        }

        var schemaLocation = location + "/schema";
        Expect(schema, schemaLocation, allowed: ["type", "properties", "required", "additionalProperties"]);

        if (String(schema, "type", schemaLocation) != "object" || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            throw Unsupported(schemaLocation, "a form that is not an object with properties.");
        }

        if (properties.EnumerateObject().ToList() is not [var part])
        {
            throw Unsupported(schemaLocation + "/properties", "a form with other than one named part. Only the JSON body is read as a named part.");
        }

        var partLocation = $"{schemaLocation}/properties/{Escape(part.Name)}";

        if (!IsToken(part.Name))
        {
            throw Unsupported(partLocation, $"the part name '{Naming.Printable(part.Name)}', which is not a token.");
        }

        Expect(part.Value, partLocation, allowed: ["contentMediaType", "contentSchema"]);

        if (String(part.Value, "contentMediaType", partLocation) != "application/json")
        {
            throw Unsupported(partLocation, "a named part that does not hold application/json.");
        }

        if (!part.Value.TryGetProperty("contentSchema", out var contentSchema))
        {
            throw Unsupported(partLocation, "a JSON part without a schema.");
        }

        Expect(contentSchema, partLocation + "/contentSchema", allowed: ["$ref"]);

        if (SchemaName(String(contentSchema, "$ref", partLocation), partLocation) != jsonSchema)
        {
            throw Unsupported(partLocation, $"a JSON part whose schema is not {jsonSchema}, the schema of the application/json body.");
        }

        if (!schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array
            || required.EnumerateArray().ToList() is not [{ ValueKind: JsonValueKind.String } only]
            || only.GetString() != part.Name)
        {
            throw Unsupported(schemaLocation + "/required", $"a form that does not require its '{part.Name}' part and nothing else.");
        }

        if (!schema.TryGetProperty("additionalProperties", out var files))
        {
            throw Unsupported(schemaLocation, "a form without file parts.");
        }

        var filesLocation = schemaLocation + "/additionalProperties";
        Expect(files, filesLocation, allowed: ["type", "contentMediaType"]);

        var fileMediaType = String(files, "contentMediaType", filesLocation);

        if (String(files, "type", filesLocation) != "string" || fileMediaType != "image/*")
        {
            throw Unsupported(filesLocation, $"file parts of '{Naming.Printable(fileMediaType)}'. The runtime sends image/png and image/jpeg parts, which only image/* admits here.");
        }

        if (!media.TryGetProperty("encoding", out var encoding))
        {
            throw Unsupported(location, $"a form that does not say how its '{part.Name}' part is encoded.");
        }

        var encodingLocation = location + "/encoding";
        Expect(encoding, encodingLocation, allowed: [part.Name]);

        if (!encoding.TryGetProperty(part.Name, out var partEncoding))
        {
            throw Unsupported(encodingLocation, $"a form that does not say how its '{part.Name}' part is encoded.");
        }

        Expect(partEncoding, $"{encodingLocation}/{Escape(part.Name)}", allowed: ["contentType"]);

        if (String(partEncoding, "contentType", $"{encodingLocation}/{Escape(part.Name)}") != "application/json")
        {
            throw Unsupported($"{encodingLocation}/{Escape(part.Name)}", $"a '{part.Name}' part encoded as something other than application/json.");
        }

        return new FormDef(part.Name, fileMediaType);
    }

    private static List<SchemaDef> ParseSchemas(JsonElement schemas, NamingOverrides overrides, IReadOnlySet<string> pinnedMaxima)
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
                membership.Tag,
                pinnedMaxima));
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

    /// <summary>
    /// Every reference names a schema the document declares. One that does not — a schema renamed or
    /// dropped while something still points at it — stops generation here, with where it was found,
    /// rather than wherever the missing name is first looked up.
    /// </summary>
    private static void ExpectReferencesResolve(IReadOnlyList<OperationDef> operations, IReadOnlyList<SchemaDef> schemas)
    {
        var declared = schemas.Select(s => s.WireName).ToHashSet(StringComparer.Ordinal);

        void Resolve(string name, string location)
        {
            if (!declared.Contains(name))
            {
                throw Unsupported(location, $"a reference to '{Naming.Printable(name)}', which the document does not declare.");
            }
        }

        foreach (var operation in operations)
        {
            var location = $"#/paths/{Escape(operation.Key.Path)}/{operation.Key.Method.ToLowerInvariant()}";

            if (operation.RequestSchema is { } request)
            {
                Resolve(request, location + "/requestBody");
            }

            foreach (var response in operation.Responses.Where(r => r.Schema is not null))
            {
                Resolve(response.Schema!, $"{location}/responses/{response.Status}");
            }
        }

        foreach (var schema in schemas)
        {
            var location = $"#/components/schemas/{schema.WireName}";

            switch (schema)
            {
                case UnionSchemaDef union:
                    foreach (var member in union.Members)
                    {
                        Resolve(member.SchemaWireName, location + "/discriminator/mapping");
                    }

                    break;

                case ObjectSchemaDef obj:
                    foreach (var property in obj.Properties)
                    {
                        foreach (var reference in References(property.Type))
                        {
                            Resolve(reference, $"{location}/properties/{Escape(property.WireName)}");
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<string> References(TypeRef type) => type switch
    {
        SchemaRef s => [s.WireName],
        NullableRef n => [n.Inner.WireName],
        MapRef m => References(m.Value),
        ArrayRef a => References(a.Items),
        _ => [],
    };

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

    private static ObjectSchemaDef ParseObject(string name, JsonElement schema, string csharpName, string? union, string? tag, IReadOnlySet<string> pinnedMaxima)
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
            var type = ParseType(property.Value, propertyLocation, pinnedMaxima);
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

        var constants = result.Where(p => p.Type is ConstRef).ToList();

        if (union is not null)
        {
            // The discriminator is written and read by the serializer, not by a generated property,
            // so it must be the member's only constant.
            if (constants is not [{ Type: ConstRef constant, Required: true }] || constant.Value != tag)
            {
                throw Unsupported(location, $"a member of '{union}' whose only constant is not a required discriminator '{tag}'.");
            }
        }
        else if (constants.FirstOrDefault(p => p.Required) is { } requiredConstant)
        {
            // An optional one is absent or that value. A required one would be a discriminator
            // without a union, which nothing here would write.
            throw Unsupported(location, $"the required constant '{Naming.Printable(requiredConstant.WireName)}' outside a discriminated union.");
        }

        return new ObjectSchemaDef(name, csharpName, result, union, tag);
    }

    /// <summary>The type of a property, an item or a map value.</summary>
    internal static TypeRef ParseType(JsonElement schema, string location, IReadOnlySet<string> pinnedMaxima)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw Unsupported(location, $"a schema of kind {schema.ValueKind}.");
        }

        if (schema.TryGetProperty("x-accepts", out var accepts))
        {
            ExpectAcceptsMatchesAnyOf(schema, accepts, location);
        }

        var keys = schema.EnumerateObject().Select(p => p.Name).Where(k => !Annotations.Contains(k) && k != "x-accepts").ToHashSet(StringComparer.Ordinal);

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
            return ParseAnyOf(schema.GetProperty("anyOf"), location, pinnedMaxima);
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

        if (keys.Contains("enum"))
        {
            Only(keys, location, "enum", "type");

            if (String(schema, "type", location) != "string")
            {
                throw Unsupported(location, "an enum that is not of strings.");
            }

            var listed = schema.GetProperty("enum");

            if (listed.ValueKind != JsonValueKind.Array || listed.GetArrayLength() == 0 || listed.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                throw Unsupported(location, "an enum that is not a non-empty list of strings.");
            }

            var values = listed.EnumerateArray().Select(v => v.GetString()!).ToList();

            if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            {
                throw Unsupported(location, "an enum that lists a value twice.");
            }

            return new EnumRef(values);
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
                    Only(keys, location, "type", "additionalProperties", "minProperties", "maxProperties");
                    ExpectPinnedMaximum(keys, "maxProperties", location, pinnedMaxima);

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

                    var value = Element(ParseType(additional, location + "/additionalProperties", pinnedMaxima), location + "/additionalProperties");
                    return new MapRef(value, OptionalCount(schema, "minProperties", location));
                }

            case "array":
                {
                    Only(keys, location, "type", "items", "minItems", "maxItems");
                    ExpectPinnedMaximum(keys, "maxItems", location, pinnedMaxima);

                    var items = schema.TryGetProperty("items", out var itemSchema)
                        ? Element(ParseType(itemSchema, location + "/items", pinnedMaxima), location + "/items")
                        : new JsonValueRef(JsonKinds.Any);

                    return new ArrayRef(items, OptionalCount(schema, "minItems", location));
                }

            default:
                throw Unsupported(location, $"type '{String(schema, "type", location)}'.");
        }
    }

    /// <summary>
    /// The element of a list or a map. An enum or a constant is read as a property's own type only:
    /// nothing in the contract has one as an element, and the generated code would have no check for it.
    /// </summary>
    private static TypeRef Element(TypeRef element, string location)
        => element is EnumRef or ConstRef
            ? throw Unsupported(location, "an enum or a constant as the element of a list or a map.")
            : element;

    /// <summary>
    /// An <c>anyOf</c> is either a set of value kinds or a nullable reference. Nothing else is interpreted.
    /// </summary>
    private static TypeRef ParseAnyOf(JsonElement branches, string location, IReadOnlySet<string> pinnedMaxima)
    {
        var list = branches.EnumerateArray().ToList();

        if (list.Count == 2
            && list.Count(IsNullBranch) == 1
            && list.SingleOrDefault(b => !IsNullBranch(b)) is { ValueKind: JsonValueKind.Object } other
            && other.TryGetProperty("$ref", out _))
        {
            var reference = ParseType(other, location + "/anyOf", pinnedMaxima);

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

    /// <summary>
    /// <c>x-accepts</c> restates in words the kinds the <c>anyOf</c> beside it admits, null aside
    /// (<c>"string | object | array"</c>). It cannot change the wire, so it is read past, but only
    /// while it says what the schema says: a restatement that disagrees is a document to look at.
    /// </summary>
    private static void ExpectAcceptsMatchesAnyOf(JsonElement schema, JsonElement accepts, string location)
    {
        if (accepts.ValueKind != JsonValueKind.String || !schema.TryGetProperty("anyOf", out var anyOf) || anyOf.ValueKind != JsonValueKind.Array)
        {
            throw Unsupported(location, "'x-accepts' that is not a string beside an anyOf.");
        }

        var said = accepts.GetString()!
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);

        var admitted = anyOf.EnumerateArray()
            .Select(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "?")
            .Where(kind => kind != "null")
            .Order(StringComparer.Ordinal);

        if (!said.SequenceEqual(admitted, StringComparer.Ordinal))
        {
            throw Unsupported(location, $"'x-accepts' of '{Naming.Printable(accepts.GetString()!)}', which is not what the anyOf beside it admits.");
        }
    }

    /// <summary>
    /// A maximum is read only where <c>semantics.json</c> pins its value. The runtime enforces those
    /// limits from its constants before a request is sent; the generated types are validated only
    /// when a response is decoded, so the maximum itself is not emitted.
    /// </summary>
    private static void ExpectPinnedMaximum(HashSet<string> keys, string keyword, string location, IReadOnlySet<string> pinnedMaxima)
    {
        if (keys.Contains(keyword) && !pinnedMaxima.Contains($"{location[1..]}/{keyword}"))
        {
            throw Unsupported(location, $"'{keyword}' where {Semantics.FileName} pins no value. Nothing would enforce it.");
        }
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
