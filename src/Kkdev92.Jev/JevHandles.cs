using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev;

/// <summary>
/// Identifies a choice question in one plan, and the type its answer maps to.
/// </summary>
/// <typeparam name="T">The option value type.</typeparam>
/// <remarks>
/// <para>
/// Returned by <see cref="JevDecisionPlanBuilder.AddChoice{T}"/> and used to read the answer with
/// <see cref="JevResult.Get{T}(JevChoiceHandle{T})"/>. A handle belongs to the plan built from
/// the builder that issued it: using it with a result of another plan throws, even when that plan
/// has a question with the same id.
/// </para>
/// <para>
/// A small value that can be stored in a field and shared across threads. <c>default</c> is not a
/// handle to anything.
/// </para>
/// </remarks>
public readonly struct JevChoiceHandle<T> : IEquatable<JevChoiceHandle<T>>
    where T : notnull
{
    internal JevChoiceHandle(ChoiceQuestionDefinition<T> definition) => Definition = definition;

    internal ChoiceQuestionDefinition<T>? Definition { get; }

    /// <summary>True for <c>default</c>, which identifies no question.</summary>
    public bool IsDefault => Definition is null;

    /// <summary>The question id, or an empty string for a default handle.</summary>
    public string Id => Definition?.Id ?? string.Empty;

    /// <summary>How many options the question has.</summary>
    public int OptionCount => Definition?.Labels.Length ?? 0;

    /// <inheritdoc />
    public bool Equals(JevChoiceHandle<T> other) => ReferenceEquals(Definition, other.Definition);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JevChoiceHandle<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Definition is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Definition);

    /// <summary>The kind and id of the question.</summary>
    public override string ToString() => IsDefault ? "JevChoiceHandle (default)" : $"JevChoiceHandle ({Id})";

    /// <summary>Whether two handles identify the same question of the same plan.</summary>
    public static bool operator ==(JevChoiceHandle<T> left, JevChoiceHandle<T> right) => left.Equals(right);

    /// <summary>Whether two handles differ.</summary>
    public static bool operator !=(JevChoiceHandle<T> left, JevChoiceHandle<T> right) => !left.Equals(right);
}

/// <summary>Identifies a score question in one plan.</summary>
/// <remarks>See <see cref="JevChoiceHandle{T}"/>: the same ownership rules apply.</remarks>
public readonly struct JevScoreHandle : IEquatable<JevScoreHandle>
{
    internal JevScoreHandle(ScoreQuestionDefinition definition) => Definition = definition;

    internal ScoreQuestionDefinition? Definition { get; }

    /// <summary>True for <c>default</c>, which identifies no question.</summary>
    public bool IsDefault => Definition is null;

    /// <summary>The question id, or an empty string for a default handle.</summary>
    public string Id => Definition?.Id ?? string.Empty;

    /// <summary>How many levels the question has.</summary>
    public int LevelCount => Definition?.Levels.Length ?? 0;

    /// <inheritdoc />
    public bool Equals(JevScoreHandle other) => ReferenceEquals(Definition, other.Definition);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JevScoreHandle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Definition is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Definition);

    /// <summary>The kind and id of the question.</summary>
    public override string ToString() => IsDefault ? "JevScoreHandle (default)" : $"JevScoreHandle ({Id})";

    /// <summary>Whether two handles identify the same question of the same plan.</summary>
    public static bool operator ==(JevScoreHandle left, JevScoreHandle right) => left.Equals(right);

    /// <summary>Whether two handles differ.</summary>
    public static bool operator !=(JevScoreHandle left, JevScoreHandle right) => !left.Equals(right);
}

/// <summary>Identifies a noul (yes/no) question in one plan.</summary>
/// <remarks>See <see cref="JevChoiceHandle{T}"/>: the same ownership rules apply.</remarks>
public readonly struct JevNoulHandle : IEquatable<JevNoulHandle>
{
    internal JevNoulHandle(NoulQuestionDefinition definition) => Definition = definition;

    internal NoulQuestionDefinition? Definition { get; }

    /// <summary>True for <c>default</c>, which identifies no question.</summary>
    public bool IsDefault => Definition is null;

    /// <summary>The question id, or an empty string for a default handle.</summary>
    public string Id => Definition?.Id ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(JevNoulHandle other) => ReferenceEquals(Definition, other.Definition);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JevNoulHandle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Definition is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Definition);

    /// <summary>The kind and id of the question.</summary>
    public override string ToString() => IsDefault ? "JevNoulHandle (default)" : $"JevNoulHandle ({Id})";

    /// <summary>Whether two handles identify the same question of the same plan.</summary>
    public static bool operator ==(JevNoulHandle left, JevNoulHandle right) => left.Equals(right);

    /// <summary>Whether two handles differ.</summary>
    public static bool operator !=(JevNoulHandle left, JevNoulHandle right) => !left.Equals(right);
}
