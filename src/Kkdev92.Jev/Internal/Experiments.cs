namespace Kkdev92.Jev.Internal;

/// <summary>The diagnostic ids of the APIs marked <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute"/>.</summary>
/// <remarks>
/// Using one of those APIs is a compile error until the caller suppresses its id, which is how a
/// caller says it accepts that the API may change or go away. Each id covers one feature, so
/// accepting one does not accept another.
/// </remarks>
internal static class Experiments
{
    /// <summary>
    /// Images and box questions. TypeSafe calls both "Dreamer-ready": the request format, the
    /// answers and the limits can change between its releases.
    /// </summary>
    public const string Images = "JEV001";
}
