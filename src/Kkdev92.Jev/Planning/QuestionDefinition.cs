using System.Text.Json;

namespace Kkdev92.Jev.Planning;

/// <summary>
/// The identity a plan's handles and its questions share.
/// </summary>
/// <remarks>
/// A small object of its own rather than the builder or the plan, so that a handle can prove which
/// plan it belongs to without keeping either of them — or their mutable state — reachable. Compared
/// by reference only: two builders never share one, even when their questions are identical.
/// </remarks>
internal sealed class PlanToken;

/// <summary>What kind of question a slot holds.</summary>
internal enum QuestionKind : byte
{
    Noul = 1,
    Choice = 2,
    Score = 3,
}

/// <summary>A question as registered: validated, copied, and fixed in its slot.</summary>
/// <remarks>
/// Deliberately non-generic below <see cref="ChoiceQuestionDefinition{T}"/>. The response reader,
/// the request writer and the consistency checks work on this type, so they are compiled once
/// however many option types a program uses — which matters under Native AOT, where every generic
/// instantiation over a value type is code in the binary.
/// </remarks>
internal abstract class QuestionDefinition(PlanToken owner, int slot, string id, JevContent instructions)
{
    public PlanToken Owner { get; } = owner;

    public int Slot { get; } = slot;

    public string Id { get; } = id;

    public JevContent Instructions { get; } = instructions;

    public abstract QuestionKind Kind { get; }

    /// <summary>How many probabilities the answer carries: one per option or level, none for a noul.</summary>
    public abstract int OutcomeCount { get; }

    /// <summary>Where this question's probabilities start in a result's shared array. Set when the plan is built.</summary>
    public int ProbabilityOffset { get; set; }

    /// <summary>The <c>type</c> discriminator.</summary>
    public string TypeTag => Kind switch
    {
        QuestionKind.Noul => "noul",
        QuestionKind.Choice => "choice",
        QuestionKind.Score => "score",
        _ => throw new InvalidOperationException("Unknown question kind."),
    };

    /// <summary>Writes the question object, <c>{"type":…,"instructions":…,"criteria":…}</c>.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        // The discriminator first. The API does not require it, but a reader that dispatches on it
        // before it has seen the rest never has to buffer.
        writer.WriteString("type"u8, TypeTag);

        if (!Instructions.IsUnspecified)
        {
            writer.WritePropertyName("instructions"u8);
            Instructions.WriteTo(writer);
        }

        WriteCriteria(writer);
        writer.WriteEndObject();
    }

    protected abstract void WriteCriteria(Utf8JsonWriter writer);
}

/// <summary>A yes/no question.</summary>
internal sealed class NoulQuestionDefinition(PlanToken owner, int slot, string id, JevContent instructions, JevNoulCriteria criteria)
    : QuestionDefinition(owner, slot, id, instructions)
{
    public JevNoulCriteria Criteria { get; } = criteria;

    public override QuestionKind Kind => QuestionKind.Noul;

    public override int OutcomeCount => 0;

    protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        if (Criteria.IsEmpty)
        {
            return;
        }

        writer.WritePropertyName("criteria"u8);
        writer.WriteStartObject();

        if (!Criteria.True.IsUnspecified)
        {
            writer.WritePropertyName("true"u8);
            Criteria.True.WriteTo(writer);
        }

        if (!Criteria.False.IsUnspecified)
        {
            writer.WritePropertyName("false"u8);
            Criteria.False.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

/// <summary>A choice question, without its value type: everything the wire needs.</summary>
internal abstract class ChoiceQuestionDefinition : QuestionDefinition
{
    private readonly Dictionary<string, int> _labelIndex;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _labelLookup;

    protected ChoiceQuestionDefinition(PlanToken owner, int slot, string id, JevContent instructions, string[] labels, JevContent[] descriptions, Dictionary<string, int> labelIndex)
        : base(owner, slot, id, instructions)
    {
        Labels = labels;
        Descriptions = descriptions;
        _labelIndex = labelIndex;
        _labelLookup = labelIndex.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The labels, in the order they were given.</summary>
    public string[] Labels { get; }

    public JevContent[] Descriptions { get; }

    public override QuestionKind Kind => QuestionKind.Choice;

    public override int OutcomeCount => Labels.Length;

    /// <summary>The index of a label, by exact ordinal match, or -1.</summary>
    public int IndexOf(string label) => _labelIndex.TryGetValue(label, out var index) ? index : -1;

    /// <summary>The index of a decoded label, without allocating a string for it, or -1.</summary>
    public int IndexOf(ReadOnlySpan<char> label) => _labelLookup.TryGetValue(label, out var index) ? index : -1;

    protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("criteria"u8);
        writer.WriteStartObject();

        for (var i = 0; i < Labels.Length; i++)
        {
            writer.WritePropertyName(Labels[i]);
            Descriptions[i].WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

/// <summary>A choice question with the values its options map to.</summary>
internal sealed class ChoiceQuestionDefinition<T>(PlanToken owner, int slot, string id, JevContent instructions, T[] values, string[] labels, JevContent[] descriptions, Dictionary<string, int> labelIndex)
    : ChoiceQuestionDefinition(owner, slot, id, instructions, labels, descriptions, labelIndex)
    where T : notnull
{
    public T[] Values { get; } = values;
}

/// <summary>A score question.</summary>
internal sealed class ScoreQuestionDefinition(PlanToken owner, int slot, string id, JevContent instructions, JevContent[] levels)
    : QuestionDefinition(owner, slot, id, instructions)
{
    public JevContent[] Levels { get; } = levels;

    public override QuestionKind Kind => QuestionKind.Score;

    public override int OutcomeCount => Levels.Length;

    protected override void WriteCriteria(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("criteria"u8);
        writer.WriteStartArray();

        foreach (var level in Levels)
        {
            level.WriteTo(writer);
        }

        writer.WriteEndArray();
    }
}
