using System.Text.Json;

namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>An operation named by method and path, which is how this repository identifies one.</summary>
/// <remarks>
/// Not by <c>operationId</c>. The document carries <c>systemone_v1_systemone_post</c>, an id
/// generated from the route; naming the public surface after it would tie the SDK to how the
/// service generates its ids.
/// </remarks>
internal sealed record OperationKey(string Method, string Path)
{
    public override string ToString() => $"{Method} {Path}";
}

/// <summary><c>public-surface.json</c>: which operations the SDK exposes, and which it refuses to.</summary>
internal sealed record PublicSurface(IReadOnlyList<ApprovedOperation> Operations, IReadOnlyList<OperationKey> Excluded)
{
    public const string FileName = "public-surface.json";

    public static PublicSurface Read(string path)
    {
        using var document = SpecJson.Parse(path);
        var root = document.RootElement;

        var operations = root.GetProperty("operations").EnumerateArray()
            .Select(e => new ApprovedOperation(
                new OperationKey(SpecJson.String(e, "method"), SpecJson.String(e, "path")),
                SpecJson.String(e, "exposedAs"),
                SpecJson.String(e, "constant")))
            .ToList();

        var excluded = root.GetProperty("excluded").EnumerateArray()
            .Select(e => new OperationKey(SpecJson.String(e, "method"), SpecJson.String(e, "path")))
            .ToList();

        return new PublicSurface(operations, excluded);
    }
}

/// <summary>An operation the SDK exposes, the member that exposes it, and the name its generated constants take.</summary>
/// <remarks>
/// The constant name is written down rather than derived. The only candidates are the path's last
/// segment, which gives <c>Systemone</c>, and the <c>operationId</c>, which is generated.
/// </remarks>
internal sealed record ApprovedOperation(OperationKey Key, string ExposedAs, string ConstantName);

/// <summary><c>naming-overrides.json</c>: C# names that the naming rule would get wrong.</summary>
internal sealed record NamingOverrides(IReadOnlyDictionary<string, string> Schemas)
{
    public const string FileName = "naming-overrides.json";

    public static NamingOverrides Read(string path)
    {
        using var document = SpecJson.Parse(path);

        var schemas = document.RootElement.GetProperty("schemas").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? throw new InvalidDataException($"Override for '{p.Name}' is not a string."), StringComparer.Ordinal);

        return new NamingOverrides(schemas);
    }
}

/// <summary>
/// <c>semantics.json</c>: what the OpenAPI document cannot say, each entry with where it came from.
/// </summary>
internal sealed record Semantics(
    IReadOnlyList<SemanticConstant> Constants,
    IReadOnlyList<SemanticConflict> Conflicts)
{
    public const string FileName = "semantics.json";

    /// <summary>The kinds of evidence an entry may rest on. Anything else is refused.</summary>
    public static readonly IReadOnlySet<string> Bases = new HashSet<string>(StringComparer.Ordinal)
    {
        // A TypeSafe documentation page says so.
        "documented",
        // Seen on the wire. The evidence is a dated observation recorded in the entry.
        "observed",
        // Nothing upstream settles it; this SDK chose, and the note says why.
        "decision",
    };

    public static Semantics Read(string path)
    {
        using var document = SpecJson.Parse(path);
        var root = document.RootElement;

        var constants = root.GetProperty("constants").EnumerateArray()
            .Select(SemanticConstant.Read)
            .ToList();

        var conflicts = root.GetProperty("conflicts").EnumerateArray()
            .Select(SemanticConflict.Read)
            .ToList();

        return new Semantics(constants, conflicts);
    }
}

/// <summary>A value the generated contract exposes as a constant: a limit, a header name, a status code.</summary>
internal sealed record SemanticConstant(string Name, JsonValueKind Kind, string? StringValue, long? IntegerValue, string Basis, int SourceCount, string Note)
{
    public static SemanticConstant Read(JsonElement element)
    {
        var name = SpecJson.String(element, "name");
        var value = element.GetProperty("value");
        var basis = SpecJson.String(element, "basis");
        var note = SpecJson.String(element, "note");
        var sources = element.TryGetProperty("sources", out var s) ? s.GetArrayLength() : 0;

        return value.ValueKind switch
        {
            JsonValueKind.String => new SemanticConstant(name, JsonValueKind.String, value.GetString(), null, basis, sources, note),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => new SemanticConstant(name, JsonValueKind.Number, null, integer, basis, sources, note),
            _ => throw new InvalidDataException($"Constant '{name}' must be a string or an integer."),
        };
    }
}

/// <summary>
/// A place where TypeSafe's descriptions of the API differ, with the resolution this SDK chose and
/// the checks that keep the resolution honest.
/// </summary>
/// <remarks>
/// The checks are assertions about the committed OpenAPI document. They are what turns a comment
/// into a gate: if a later snapshot stops saying what the resolution was based on, generation stops
/// and somebody has to decide again.
/// </remarks>
internal sealed record SemanticConflict(string Id, string Summary, string Resolution, IReadOnlyList<SpecCheck> Checks, int SourceCount)
{
    public static SemanticConflict Read(JsonElement element)
    {
        var checks = element.GetProperty("checks").EnumerateArray().Select(SpecCheck.Read).ToList();
        var sources = element.GetProperty("sources").GetArrayLength();

        return new SemanticConflict(
            SpecJson.String(element, "id"),
            SpecJson.String(element, "summary"),
            SpecJson.String(element, "resolution"),
            checks,
            sources);
    }
}

/// <summary>One assertion about the document at a JSON pointer.</summary>
internal sealed record SpecCheck(string Pointer, string Operator, JsonElement? Operand)
{
    public static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.Ordinal)
    {
        "present", "absent", "equals", "contains", "notContains",
    };

    public static SpecCheck Read(JsonElement element)
    {
        var pointer = SpecJson.String(element, "pointer");

        foreach (var candidate in Operators)
        {
            if (element.TryGetProperty(candidate, out var operand))
            {
                return new SpecCheck(pointer, candidate, candidate is "present" or "absent" ? null : operand.Clone());
            }
        }

        throw new InvalidDataException($"The check at '{pointer}' names no operator. Expected one of: {string.Join(", ", Operators)}.");
    }

    public override string ToString() => Operand is { } operand
        ? $"{Pointer} {Operator} {operand.GetRawText()}"
        : $"{Pointer} {Operator}";
}

/// <summary>Strict JSON reading shared by the companion files.</summary>
internal static class SpecJson
{
    public static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON: {ex.Message}", ex);
        }
    }

    public static string String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Expected a string '{name}' in {element.GetRawText()}.");
}
