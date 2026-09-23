namespace Kkdev92.Jev;

/// <summary>
/// One option of a choice question: the value your code works with, the label sent to the model,
/// and a description of when the option applies.
/// </summary>
/// <typeparam name="T">
/// The value type. Any type, not only an enum. Values are held by reference, not copied, so a
/// reference-type value should not be mutated after the plan is built.
/// </typeparam>
/// <remarks>
/// The label is not an identifier the model ignores: the model reads it, together with the
/// description, to decide. Choose labels that mean something. The SDK sends them exactly as given
/// and maps the answer back to <see cref="Value"/> by exact, ordinal match.
/// </remarks>
public readonly struct JevChoiceOption<T>
    where T : notnull
{
    /// <summary>An option with no description: the model judges it by its label alone.</summary>
    /// <param name="value">The value an answer choosing this option maps to.</param>
    /// <param name="label">The option's name on the wire.</param>
    public JevChoiceOption(T value, string label)
        : this(value, label, JevContent.Null)
    {
    }

    /// <summary>An option with a description.</summary>
    /// <param name="value">The value an answer choosing this option maps to.</param>
    /// <param name="label">The option's name on the wire.</param>
    /// <param name="description">
    /// When the option applies: text, an object or an array. <see cref="JevContent.Null"/> for none.
    /// <c>default</c> is refused when the option is added, because a missing description would be
    /// indistinguishable from a forgotten one.
    /// </param>
    public JevChoiceOption(T value, string label, JevContent description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    /// <summary>The value an answer choosing this option maps to.</summary>
    public T Value { get; }

    /// <summary>The option's name on the wire.</summary>
    public string Label { get; }

    /// <summary>When the option applies, or <see cref="JevContent.Null"/>.</summary>
    public JevContent Description { get; }

    /// <summary>The label. Never the description.</summary>
    public override string ToString() => Label ?? string.Empty;
}
