using System.Text.Json;
using Kkdev92.Jev.Serialization;

namespace Kkdev92.Jev.Wire;

/// <summary>
/// Collects the ways a wire model breaks the contract that the serializer itself does not check.
/// </summary>
/// <remarks>
/// <para>
/// The source-generated serializer enforces required members, nullability, duplicate keys and the
/// discriminator. It does not know that <c>state</c> may be a string, an object or an array but not
/// a number, that <c>questions</c> needs at least one entry, that a number which overflowed to
/// infinity is not a number the service sent, or that an entry in a collection must not be null.
/// The generated <c>Validate</c> methods check those against this collector.
/// </para>
/// <para>
/// Paths are for tests and for the reference decoder's diagnostics. They can contain question ids
/// and labels, so they never reach an exception message.
/// </para>
/// </remarks>
internal sealed class WireValidation
{
    private List<WireViolation>? _violations;

    /// <summary>True when nothing has been recorded.</summary>
    public bool IsValid => _violations is null;

    /// <summary>What was recorded, in the order it was found.</summary>
    public IReadOnlyList<WireViolation> Violations => _violations ?? (IReadOnlyList<WireViolation>)[];

    public static string Member(string path, string name) => path + "." + name;

    public static string Key(string path, string key) => path + "[\"" + key + "\"]";

    public static string Index(string path, int index) => path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    /// <summary>A required value, which must be present and of one of the kinds.</summary>
    public void Kinds(JsonElement value, JsonKinds kinds, string path)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            Add(path, WireRule.Missing);
            return;
        }

        if (!Admits(value, kinds))
        {
            Add(path, WireRule.Kind);
        }
    }

    /// <summary>An optional value, which may be absent but otherwise must be of one of the kinds.</summary>
    public void OptionalKinds(JsonElement value, JsonKinds kinds, string path)
    {
        if (value.ValueKind != JsonValueKind.Undefined && !Admits(value, kinds))
        {
            Add(path, WireRule.Kind);
        }
    }

    /// <summary>A number the service sent, which JSON cannot spell as infinite or NaN; one that overflowed was not a real value.</summary>
    public void Finite(double value, string path)
    {
        if (!double.IsFinite(value))
        {
            Add(path, WireRule.NotFinite);
        }
    }

    public void MinCount(int count, int minimum, string path)
    {
        if (count < minimum)
        {
            Add(path, WireRule.TooFew);
        }
    }

    /// <summary>A null entry in a collection, which the serializer's nullability checks do not cover.</summary>
    public void Null(string path) => Add(path, WireRule.Null);

    /// <summary>True when the value's kind is one the contract admits.</summary>
    public static bool Admits(JsonElement value, JsonKinds kinds) => value.ValueKind switch
    {
        JsonValueKind.String => kinds.HasFlag(JsonKinds.String),
        JsonValueKind.Object => kinds.HasFlag(JsonKinds.Object),
        JsonValueKind.Array => kinds.HasFlag(JsonKinds.Array),
        JsonValueKind.Null => kinds.HasFlag(JsonKinds.Null),
        JsonValueKind.True or JsonValueKind.False => kinds.HasFlag(JsonKinds.Boolean),
        JsonValueKind.Number => kinds.HasFlag(JsonKinds.Number)
            || (kinds.HasFlag(JsonKinds.Integer) && IsInteger(value)),
        _ => false,
    };

    private static bool IsInteger(JsonElement value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value.GetRawText());
        return JsonExactInteger.TryParseInt64(raw, out _);
    }

    private void Add(string path, WireRule rule) => (_violations ??= []).Add(new WireViolation(path, rule));
}

/// <summary>One way a wire model breaks the contract.</summary>
internal readonly record struct WireViolation(string Path, WireRule Rule);

internal enum WireRule
{
    Missing,
    Kind,
    NotFinite,
    TooFew,
    Null,
}
