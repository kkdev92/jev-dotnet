namespace Kkdev92.Jev;

/// <summary>
/// Optional descriptions of what counts as yes and what counts as no, for a noul question.
/// </summary>
/// <remarks>
/// Each side is independent: leave it <c>default</c> to omit it, set <see cref="JevContent.Null"/>
/// to send an explicit <c>null</c>, or give text, an object or an array. When both sides are
/// omitted, so is the whole <c>criteria</c> object. TypeSafe suggests trying a question with and
/// without criteria and keeping whichever answers your data better.
/// </remarks>
public readonly struct JevNoulCriteria : IEquatable<JevNoulCriteria>
{
    /// <summary>Descriptions of both outcomes.</summary>
    /// <param name="whenTrue">What counts as yes: an answer near 1. Sent as <c>true</c>.</param>
    /// <param name="whenFalse">What counts as no: an answer near 0. Sent as <c>false</c>.</param>
    public JevNoulCriteria(JevContent whenTrue, JevContent whenFalse)
    {
        True = whenTrue;
        False = whenFalse;
    }

    /// <summary>What counts as yes. Sent as <c>criteria.true</c>.</summary>
    public JevContent True { get; }

    /// <summary>What counts as no. Sent as <c>criteria.false</c>.</summary>
    public JevContent False { get; }

    /// <summary>True when neither side is given, and <c>criteria</c> is left out of the request.</summary>
    public bool IsEmpty => True.IsUnspecified && False.IsUnspecified;

    /// <inheritdoc />
    public bool Equals(JevNoulCriteria other) => True.Equals(other.True) && False.Equals(other.False);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JevNoulCriteria other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(True, False);

    /// <summary>The kinds of the two sides. Never their content.</summary>
    public override string ToString() => $"true: {True}, false: {False}";

    /// <summary>Whether two criteria are equal.</summary>
    public static bool operator ==(JevNoulCriteria left, JevNoulCriteria right) => left.Equals(right);

    /// <summary>Whether two criteria differ.</summary>
    public static bool operator !=(JevNoulCriteria left, JevNoulCriteria right) => !left.Equals(right);
}
