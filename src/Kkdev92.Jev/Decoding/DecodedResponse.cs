using System.Collections.Immutable;
using System.Text.Json;

namespace Kkdev92.Jev.Decoding;

/// <summary>A successful response, checked against the plan and laid out by slot.</summary>
/// <remarks>
/// Everything here is owned by the result that will hold it: the arrays are fresh, the legend
/// values were parsed into documents of their own, and nothing refers back to the response buffer.
/// That buffer can be reused or dropped the moment decoding returns.
/// </remarks>
internal sealed class DecodedResponse(string model, JevUsage usage, AnswerSlot[] slots, double[] probabilities, ImmutableArray<JsonElement>[]? legends)
{
    public string Model { get; } = model;

    public JevUsage Usage { get; } = usage;

    /// <summary>One per question, in plan order.</summary>
    public AnswerSlot[] Slots { get; } = slots;

    /// <summary>Every question's probabilities, each at the offset its definition records.</summary>
    public double[] Probabilities { get; } = probabilities;

    /// <summary>One per question when the plan has score questions; default for the others. Null when it has none.</summary>
    public ImmutableArray<JsonElement>[]? Legends { get; } = legends;
}

/// <summary>The scalar part of one answer.</summary>
internal struct AnswerSlot
{
    /// <summary>The noul probability, or the score's expected value.</summary>
    public double Value;

    /// <summary>The confidence of a choice or a score. A noul has none, and this stays NaN.</summary>
    public double Confidence;

    /// <summary>The index of the chosen option, for a choice.</summary>
    public int Selected;
}
