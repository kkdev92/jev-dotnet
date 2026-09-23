namespace Kkdev92.Jev.Wire;

/// <summary>The JSON value kinds a field typed as an untyped value may take, as the contract declares them.</summary>
/// <remarks>
/// The generated validators name these, so the members have to match the generator's
/// <c>JsonKinds</c> one for one.
/// </remarks>
[Flags]
internal enum JsonKinds
{
    None = 0,
    String = 1,
    Number = 2,

    /// <summary>A number with no fractional part, however it is spelled: 3, 3.0 and 3e0 all qualify.</summary>
    Integer = 4,
    Boolean = 8,
    Object = 16,
    Array = 32,
    Null = 64,
    Any = String | Number | Integer | Boolean | Object | Array | Null,
}
