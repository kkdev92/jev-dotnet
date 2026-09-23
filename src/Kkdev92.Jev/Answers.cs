using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev;

/// <summary>The answer to a choice question.</summary>
/// <typeparam name="T">The option value type.</typeparam>
/// <remarks>
/// <para>
/// A view over data the <see cref="JevResult"/> owns: reading it allocates nothing, and the value is
/// the one you supplied with the option, never boxed or re-parsed.
/// </para>
/// <para>
/// <see cref="Confidence"/> is the service's own summary of how peaked the distribution is — not an
/// accuracy, and not the probability of the chosen option. The full distribution is in
/// <see cref="Probabilities"/> for any other measure. Neither is altered by the SDK: nothing is
/// renormalised, rounded or recomputed.
/// </para>
/// <para>
/// <see cref="ToString"/> reports the option count. Never the chosen option, which is data.
/// </para>
/// </remarks>
public readonly struct ChoiceAnswer<T>
    where T : notnull
{
    private readonly ChoiceQuestionDefinition<T>? _definition;
    private readonly double[]? _probabilities;
    private readonly AnswerSlot _slot;

    internal ChoiceAnswer(ChoiceQuestionDefinition<T> definition, double[] probabilities, AnswerSlot slot)
    {
        _definition = definition;
        _probabilities = probabilities;
        _slot = slot;
    }

    /// <summary>The value of the chosen option.</summary>
    public T Value => Definition.Values[_slot.Selected];

    /// <summary>The label of the chosen option, as the service returned it.</summary>
    public string Label => Definition.Labels[_slot.Selected];

    /// <summary>The position of the chosen option in the order the options were given.</summary>
    public int Index => Checked(_slot.Selected);

    /// <summary>The service's confidence in the choice, from 0 to 1.</summary>
    public double Confidence => Checked(_slot.Confidence);

    /// <summary>How many options the question has.</summary>
    public int OptionCount => Definition.Labels.Length;

    /// <summary>The probability of every option, in the order the options were given.</summary>
    public ReadOnlySpan<double> Probabilities => new(_probabilities, Definition.ProbabilityOffset, Definition.Labels.Length);

    /// <summary>The probability of the option at a position.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a position of an option.</exception>
    public double GetProbability(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, OptionCount);
        return _probabilities![Definition.ProbabilityOffset + index];
    }

    /// <summary>The probability of the option with a label.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No option has that label. Labels are compared ordinally.</exception>
    public double GetProbability(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var index = Definition.IndexOf(label);

        return index >= 0
            ? _probabilities![Definition.ProbabilityOffset + index]
            : throw new ArgumentException("No option of this question has that label.", nameof(label));
    }

    /// <summary>The value of the option at a position.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a position of an option.</exception>
    public T GetValue(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, OptionCount);
        return Definition.Values[index];
    }

    /// <summary>The label of the option at a position.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a position of an option.</exception>
    public string GetLabel(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, OptionCount);
        return Definition.Labels[index];
    }

    /// <summary>The option count. Never the chosen option.</summary>
    public override string ToString() => _definition is null
        ? "ChoiceAnswer (default)"
        : $"ChoiceAnswer ({OptionCount.ToString(CultureInfo.InvariantCulture)} options)";

    private ChoiceQuestionDefinition<T> Definition
        => _definition ?? throw new InvalidOperationException("This answer is default. Get answers from JevResult.Get.");

    /// <summary>Returns the value, after refusing a default answer.</summary>
    private TValue Checked<TValue>(TValue value)
    {
        _ = Definition;
        return value;
    }
}

/// <summary>The answer to a score question.</summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is the probability-weighted mean of the level indices, so it can fall between
/// two levels: 1.43 on a three-level scale leans towards level 1 with real weight on level 2.
/// Different distributions can give the same value; read <see cref="Probabilities"/> and
/// <see cref="Confidence"/> to tell them apart. TypeSafe advises against interpolating an exact
/// magnitude between two levels from it.
/// </para>
/// <para>
/// A view over data the <see cref="JevResult"/> owns. <see cref="ToString"/> reports the level count only.
/// </para>
/// </remarks>
public readonly struct ScoreAnswer
{
    private readonly ScoreQuestionDefinition? _definition;
    private readonly double[]? _probabilities;
    private readonly AnswerSlot _slot;
    private readonly ImmutableArray<JsonElement> _legend;

    internal ScoreAnswer(ScoreQuestionDefinition definition, double[] probabilities, AnswerSlot slot, ImmutableArray<JsonElement> legend)
    {
        _definition = definition;
        _probabilities = probabilities;
        _slot = slot;
        _legend = legend;
    }

    /// <summary>The expected score: from 0 to <see cref="LevelCount"/> − 1, possibly between levels.</summary>
    public double Value => Checked(_slot.Value);

    /// <summary>The service's confidence in the score, from 0 to 1.</summary>
    public double Confidence => Checked(_slot.Confidence);

    /// <summary>How many levels the question has.</summary>
    public int LevelCount => Definition.Levels.Length;

    /// <summary>The probability of every level, by level index.</summary>
    public ReadOnlySpan<double> Probabilities => new(_probabilities, Definition.ProbabilityOffset, Definition.Levels.Length);

    /// <summary>
    /// Each level's description as the service returned it, by level index: a string, an object or
    /// an array. Independent JSON values that outlive the response.
    /// </summary>
    public ImmutableArray<JsonElement> Legend => Checked(_legend);

    /// <summary>The probability of a level.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level of the question.</exception>
    public double GetProbability(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, LevelCount);
        return _probabilities![Definition.ProbabilityOffset + level];
    }

    /// <summary>The level count. Never the score.</summary>
    public override string ToString() => _definition is null
        ? "ScoreAnswer (default)"
        : $"ScoreAnswer ({LevelCount.ToString(CultureInfo.InvariantCulture)} levels)";

    private ScoreQuestionDefinition Definition
        => _definition ?? throw new InvalidOperationException("This answer is default. Get answers from JevResult.Get.");

    /// <summary>Returns the value, after refusing a default answer.</summary>
    private TValue Checked<TValue>(TValue value)
    {
        _ = Definition;
        return value;
    }
}

/// <summary>The answer to a noul (yes/no) question.</summary>
/// <remarks>
/// <para>
/// One number: the probability that the answer is yes. It is the answer and the certainty at once,
/// so there is no separate confidence — the SDK does not invent one. Where to draw the line between
/// yes, no and "ask a person" is your code's decision, and depends on what a wrong answer costs.
/// </para>
/// <para>
/// It is not a scale of the property asked about: 0.5 means uncertain, not "halfway". Use a score
/// question for degree.
/// </para>
/// </remarks>
public readonly struct NoulAnswer
{
    internal NoulAnswer(double probability) => Probability = probability;

    /// <summary>The probability of yes, from 0 (no) to 1 (yes).</summary>
    public double Probability { get; }

    /// <summary>The type name. Never the probability.</summary>
    public override string ToString() => nameof(NoulAnswer);
}

/// <summary>A model or alias the key can use, as <c>GET /v1/models</c> lists it.</summary>
/// <remarks>
/// Values are kept as the service sent them. <see cref="ReleaseDate"/> is documented as
/// <c>YYYY-MM-DD</c> but kept as text, so an unexpected format is visible rather than lost in
/// parsing. The list currently holds aliases only; versioned ids such as <c>jev-1.13.0</c> are
/// accepted whether or not they appear in it, so do not use it to decide whether a model exists.
/// </remarks>
public sealed class JevModel
{
    internal JevModel(string name, string description, string releaseDate)
    {
        Name = name;
        Description = description;
        ReleaseDate = releaseDate;
    }

    /// <summary>The name to pass as a model, such as <c>jev-latest</c>.</summary>
    public string Name { get; }

    /// <summary>What the model is for.</summary>
    public string Description { get; }

    /// <summary>When the model or alias was released, as the service wrote it.</summary>
    public string ReleaseDate { get; }

    /// <summary>The name.</summary>
    public override string ToString() => Name;
}
