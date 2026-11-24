using System.Globalization;

namespace Kkdev92.Jev;

/// <summary>An application value and its description at one explicitly ordered score level.</summary>
public readonly struct JevScoreLevel<T> where T : notnull
{
    /// <summary>Associate a value with this ordinal position in the score legend.</summary>
    public JevScoreLevel(T value, JevContent description) => (Value, Description) = (value, description);
    /// <summary>Application value at this level. Values must remain stable after registration.</summary>
    public T Value { get; }
    /// <summary>The description submitted for the level.</summary>
    public JevContent Description { get; }
}

/// <summary>Identifies one score question and its explicitly ordered application values.</summary>
public readonly struct JevScoreHandle<T> : IEquatable<JevScoreHandle<T>> where T : notnull
{
    internal JevScoreHandle(JevScoreHandle raw, T[] values) => (Raw, Values) = (raw, values);
    internal JevScoreHandle Raw { get; }
    internal T[]? Values { get; }
    /// <summary>Whether this handle identifies no question.</summary>
    public bool IsDefault => Raw.IsDefault;
    /// <summary>The question ID or the empty string on a default handle.</summary>
    public string Id => Raw.Id;
    /// <summary>Number of registered levels.</summary>
    public int LevelCount => Raw.LevelCount;
    /// <inheritdoc/>
    public bool Equals(JevScoreHandle<T> other) => Raw.Equals(other.Raw);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JevScoreHandle<T> other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => Raw.GetHashCode();
    /// <summary>Compare question identity.</summary>
    public static bool operator ==(JevScoreHandle<T> left, JevScoreHandle<T> right) => left.Equals(right);
    /// <summary>Compare question identity.</summary>
    public static bool operator !=(JevScoreHandle<T> left, JevScoreHandle<T> right) => !left.Equals(right);
    /// <inheritdoc/>
    public override string ToString() => IsDefault ? "JevScoreHandle (default)" : $"JevScoreHandle ({LevelCount.ToString(CultureInfo.InvariantCulture)} levels)";
}

/// <summary>A view of a score and its typed legend in registration order.</summary>
public readonly struct ScoreAnswer<T> where T : notnull
{
    private readonly ScoreAnswer _raw;
    private readonly T[]? _values;
    internal ScoreAnswer(ScoreAnswer raw, T[] values) => (_raw, _values) = (raw, values);
    private T[] Values => _values ?? throw new InvalidOperationException("This answer is default. Get answers from JevResult.Get.");
    /// <summary>The validated continuous score and complete distribution.</summary>
    public ScoreAnswer Raw { get { _ = Values; return _raw; } }
    /// <summary>Expected score reported by the service.</summary>
    public double Value => _raw.Value;
    /// <summary>Confidence reported by the service.</summary>
    public double Confidence => _raw.Confidence;
    /// <summary>Highest-probability level, taking the first in case of a tie.</summary>
    public T MostLikely
    {
        get
        {
            var probabilities = _raw.Probabilities;
            int best = 0;
            for (int i = 1; i < probabilities.Length; i++)
            {
                if (probabilities[i] > probabilities[best]) { best = i; }
            }
            return Values[best];
        }
    }
    /// <summary>Find an application value by ordinal.</summary>
    public T GetLevel(int index)
    {
        var values = Values;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, values.Length);
        return values[index];
    }
    /// <summary>Read the returned probability for a registered value.</summary>
    public double GetProbabilityOf(T level)
    {
        var values = Values;
        ArgumentNullException.ThrowIfNull(level);
        var index = Array.IndexOf(values, level);
        if (index < 0) { throw new ArgumentException("The level was not registered.", nameof(level)); }
        return _raw.GetProbability(index);
    }
    /// <inheritdoc/>
    public override string ToString() => _raw.ToString();
}
