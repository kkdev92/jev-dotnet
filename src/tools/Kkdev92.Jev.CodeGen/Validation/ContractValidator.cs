using System.Text.Json;
using Kkdev92.Jev.CodeGen.IntermediateModel;
using Kkdev92.Jev.CodeGen.Normalization;
using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.Validation;

/// <summary>The contract after validation: what is exposed, and which schemas that reaches.</summary>
internal sealed record ValidatedContract(
    ApiContract Contract,
    IReadOnlyList<ExposedOperation> Operations,
    IReadOnlyList<SchemaDef> ReachableSchemas,
    IReadOnlyList<string> Warnings);

/// <summary>An operation the document declares and <c>public-surface.json</c> approves.</summary>
internal sealed record ExposedOperation(OperationDef Definition, ApprovedOperation Approval);

/// <summary>
/// Checks the intermediate model against the companion files before anything is emitted.
/// </summary>
/// <remarks>
/// Every rule here is one that, broken, would otherwise surface as generated code that compiles
/// and is wrong: an operation exposed because the document happened to contain it, a resolution
/// in <c>semantics.json</c> that no longer matches the document it was based on, two wire names
/// sharing one C# name.
/// </remarks>
internal static class ContractValidator
{
    public static ValidatedContract Validate(ApiContract contract, SpecSnapshot snapshot)
    {
        var warnings = new List<string>();

        var operations = ValidateSurface(contract, snapshot.PublicSurface);
        var reachable = Reachable(contract, operations);

        foreach (var schema in contract.Schemas.Where(s => !reachable.Contains(s)))
        {
            warnings.Add($"schema '{schema.WireName}' is not reachable from an exposed operation and is not generated.");
        }

        var typeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var schema in reachable)
        {
            if (!typeNames.Add(schema.CSharpName))
            {
                throw new InvalidDataException($"Two schemas become the C# type '{schema.CSharpName}'. Add an override to {NamingOverrides.FileName}.");
            }

            // The serializer context exposes one property per type, named after it; these would
            // collide with members JsonSerializerContext already has.
            if (schema.CSharpName is "Options" or "Default" or "GeneratedSerializerOptions" or "GetTypeInfo")
            {
                throw new InvalidDataException($"'{schema.CSharpName}' collides with a JsonSerializerContext member. Add an override to {NamingOverrides.FileName}.");
            }
        }

        ValidateSemantics(snapshot);

        return new ValidatedContract(contract, operations, reachable.OrderBy(s => s.CSharpName, StringComparer.Ordinal).ToList(), warnings);
    }

    /// <summary>
    /// An operation is exposed because <c>public-surface.json</c> says so, never because the
    /// document contains it.
    /// </summary>
    private static List<ExposedOperation> ValidateSurface(ApiContract contract, PublicSurface surface)
    {
        var declared = contract.Operations.ToDictionary(o => o.Key, o => o);
        var approved = new List<ExposedOperation>();
        var constants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in surface.Operations)
        {
            if (!declared.TryGetValue(entry.Key, out var operation))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} approves {entry.Key}, which the document does not declare.");
            }

            if (surface.Excluded.Contains(entry.Key))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} both approves and excludes {entry.Key}.");
            }

            if (!Naming.IsTypeName(entry.ConstantName) || !constants.Add(entry.ConstantName))
            {
                throw new InvalidDataException($"{PublicSurface.FileName}: '{entry.ConstantName}' is not a unique PascalCase constant name.");
            }

            approved.Add(new ExposedOperation(operation, entry));
        }

        foreach (var excluded in surface.Excluded)
        {
            if (!declared.ContainsKey(excluded))
            {
                throw new InvalidDataException($"{PublicSurface.FileName} excludes {excluded}, which the document does not declare. Remove the stale entry.");
            }
        }

        var unclassified = contract.Operations
            .Where(o => !surface.Operations.Any(a => a.Key == o.Key) && !surface.Excluded.Contains(o.Key))
            .Select(o => o.Key.ToString())
            .ToList();

        if (unclassified.Count > 0)
        {
            throw new InvalidDataException(
                $"The document declares operations that {PublicSurface.FileName} neither approves nor excludes: {string.Join(", ", unclassified)}. "
                + "Classify each one; a new operation is never exposed by default.");
        }

        return approved
            .OrderBy(o => o.Definition.Key.Path, StringComparer.Ordinal)
            .ThenBy(o => o.Definition.Key.Method, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<SchemaDef> Reachable(ApiContract contract, IReadOnlyList<ExposedOperation> exposed)
    {
        var reachable = new HashSet<SchemaDef>();
        var pending = new Stack<string>();

        foreach (var operation in exposed.Select(e => e.Definition))
        {
            if (operation.RequestSchema is { } request)
            {
                pending.Push(request);
            }

            foreach (var response in operation.Responses)
            {
                if (response.Schema is { } schema)
                {
                    pending.Push(schema);
                }
            }
        }

        while (pending.TryPop(out var name))
        {
            var schema = contract.Schema(name);

            if (!reachable.Add(schema))
            {
                continue;
            }

            switch (schema)
            {
                case UnionSchemaDef union:
                    foreach (var member in union.Members)
                    {
                        pending.Push(member.SchemaWireName);
                    }

                    break;

                case ObjectSchemaDef obj:
                    foreach (var property in obj.Properties)
                    {
                        foreach (var reference in References(property.Type))
                        {
                            pending.Push(reference);
                        }
                    }

                    if (obj.Union is { } owner)
                    {
                        pending.Push(owner);
                    }

                    break;
            }
        }

        return reachable;
    }

    private static IEnumerable<string> References(TypeRef type) => type switch
    {
        SchemaRef s => [s.WireName],
        NullableRef n => [n.Inner.WireName],
        MapRef m => References(m.Value),
        ArrayRef a => References(a.Items),
        _ => [],
    };

    private static void ValidateSemantics(SpecSnapshot snapshot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var constant in snapshot.Semantics.Constants)
        {
            if (!Naming.IsTypeName(Naming.ToPascalCase(constant.Name)) || !names.Add(constant.Name))
            {
                throw new InvalidDataException($"{Semantics.FileName}: constant '{constant.Name}' is not a unique identifier.");
            }

            ValidateBasis($"constant '{constant.Name}'", constant.Basis, constant.SourceCount);
        }

        using var document = JsonDocument.Parse(snapshot.CanonicalBytes);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var conflict in snapshot.Semantics.Conflicts)
        {
            if (!ids.Add(conflict.Id))
            {
                throw new InvalidDataException($"{Semantics.FileName}: conflict '{conflict.Id}' is recorded twice.");
            }

            if (conflict.SourceCount < 2)
            {
                throw new InvalidDataException($"{Semantics.FileName}: conflict '{conflict.Id}' cites {conflict.SourceCount} source(s). A disagreement needs at least the two sources that disagree.");
            }

            if (conflict.Checks.Count == 0)
            {
                throw new InvalidDataException($"{Semantics.FileName}: conflict '{conflict.Id}' has no checks, so nothing would notice if the document stopped saying what the resolution relies on.");
            }

            foreach (var check in conflict.Checks)
            {
                if (!Holds(document.RootElement, check))
                {
                    throw new InvalidDataException(
                        $"{Semantics.FileName}: conflict '{conflict.Id}' was resolved on the basis that {check}, and the document no longer says so. "
                        + "Revisit the resolution before regenerating.");
                }
            }
        }
    }

    private static void ValidateBasis(string what, string basis, int sourceCount)
    {
        if (!Semantics.Bases.Contains(basis))
        {
            throw new InvalidDataException($"{Semantics.FileName}: {what} rests on '{basis}', which is not one of: {string.Join(", ", Semantics.Bases)}.");
        }

        if (basis != "decision" && sourceCount == 0)
        {
            throw new InvalidDataException($"{Semantics.FileName}: {what} claims '{basis}' and cites no source.");
        }
    }

    /// <summary>Evaluates one check against the canonical document.</summary>
    internal static bool Holds(JsonElement root, SpecCheck check)
    {
        var found = JsonPointer.TryResolve(root, check.Pointer, out var target);

        return check.Operator switch
        {
            "present" => found,
            "absent" => !found,
            "equals" => found && JsonElement.DeepEquals(target, check.Operand!.Value),
            "contains" => found && target.ValueKind == JsonValueKind.Array && target.EnumerateArray().Any(e => JsonElement.DeepEquals(e, check.Operand!.Value)),
            "notContains" => !found || (target.ValueKind == JsonValueKind.Array && !target.EnumerateArray().Any(e => JsonElement.DeepEquals(e, check.Operand!.Value))),
            _ => throw new InvalidDataException($"Unknown check operator '{check.Operator}'."),
        };
    }
}

/// <summary>RFC 6901 JSON pointers, for <c>semantics.json</c> checks.</summary>
internal static class JsonPointer
{
    public static bool TryResolve(JsonElement root, string pointer, out JsonElement target)
    {
        target = root;

        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            throw new InvalidDataException($"'{pointer}' is not a JSON pointer.");
        }

        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            switch (target.ValueKind)
            {
                case JsonValueKind.Object when target.TryGetProperty(segment, out var next):
                    target = next;
                    break;

                case JsonValueKind.Array when int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < target.GetArrayLength():
                    target = target[index];
                    break;

                default:
                    target = default;
                    return false;
            }
        }

        return true;
    }
}
