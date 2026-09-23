using Kkdev92.Jev.CodeGen.Specifications;

namespace Kkdev92.Jev.CodeGen.IntermediateModel;

/// <summary>
/// The contract as the generator understands it: what the OpenAPI document says, reduced to the
/// subset this API uses, with wire names and C# names kept as separate facts.
/// </summary>
/// <remarks>
/// The emitter reads only this. Anything the parser could not represent here has already failed
/// generation, so there is no path by which a keyword the generator does not understand is quietly
/// dropped on the way to C#.
/// </remarks>
internal sealed record ApiContract(
    string Contract,
    string OpenApiVersion,
    string Title,
    string ApiVersion,
    string SnapshotSha256,
    IReadOnlyList<OperationDef> Operations,
    IReadOnlyList<SchemaDef> Schemas,
    IReadOnlyList<SemanticConstant> Constants)
{
    public SchemaDef Schema(string wireName)
        => Schemas.FirstOrDefault(s => string.Equals(s.WireName, wireName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No schema '{wireName}' in the contract.");
}

/// <summary>An HTTP operation.</summary>
internal sealed record OperationDef(
    OperationKey Key,
    string? OperationId,
    string? RequestSchema,
    bool RequestRequired,
    IReadOnlyList<ResponseDef> Responses,
    IReadOnlyList<string> SecuritySchemes);

/// <summary>A declared response: its status and, when it has a JSON body, the schema of that body.</summary>
internal sealed record ResponseDef(string Status, string? Schema);

/// <summary>A named schema under <c>components/schemas</c>.</summary>
internal abstract record SchemaDef(string WireName, string CSharpName);

/// <summary>An object with declared properties.</summary>
/// <param name="Union">The union this object is a member of, if any.</param>
/// <param name="DiscriminatorValue">The value of the union's discriminator property that selects this object.</param>
internal sealed record ObjectSchemaDef(
    string WireName,
    string CSharpName,
    IReadOnlyList<PropertyDef> Properties,
    string? Union,
    string? DiscriminatorValue) : SchemaDef(WireName, CSharpName);

/// <summary>A <c>oneOf</c> of objects told apart by a discriminator property.</summary>
internal sealed record UnionSchemaDef(
    string WireName,
    string CSharpName,
    string DiscriminatorProperty,
    IReadOnlyList<UnionMember> Members) : SchemaDef(WireName, CSharpName);

/// <summary>One member of a union: the discriminator value and the schema it selects.</summary>
internal sealed record UnionMember(string Tag, string SchemaWireName);

/// <summary>A property of an object schema.</summary>
internal sealed record PropertyDef(string WireName, string CSharpName, TypeRef Type, bool Required);

/// <summary>What a property, an item or a map value can hold.</summary>
internal abstract record TypeRef;

/// <summary>A JSON string, number or integer, typed as such in C#.</summary>
internal sealed record PrimitiveRef(PrimitiveKind Kind) : TypeRef;

/// <summary>A reference to a named schema.</summary>
internal sealed record SchemaRef(string WireName) : TypeRef;

/// <summary>A reference to a named schema, or <c>null</c>.</summary>
internal sealed record NullableRef(SchemaRef Inner) : TypeRef;

/// <summary>An object used as a map from arbitrary keys to one value type.</summary>
internal sealed record MapRef(TypeRef Value, int? MinProperties) : TypeRef;

/// <summary>An array of one item type.</summary>
internal sealed record ArrayRef(TypeRef Items, int? MinItems) : TypeRef;

/// <summary>An untyped JSON value restricted to a set of kinds, held as a <c>JsonElement</c>.</summary>
/// <remarks>
/// This is how <c>anyOf [string, object, array, null]</c> arrives, which is the shape of every
/// piece of content the API accepts: a state, an instruction, a criterion. Holding it as a
/// <c>JsonElement</c> keeps absent and <c>null</c> apart, which a <c>string?</c> could not.
/// </remarks>
internal sealed record JsonValueRef(JsonKinds Kinds) : TypeRef;

/// <summary>A string property whose value is fixed. Only valid as a union's discriminator.</summary>
internal sealed record ConstRef(string Value) : TypeRef;

internal enum PrimitiveKind
{
    String,
    Number,
    Integer,
}

/// <summary>The JSON value kinds a <see cref="JsonValueRef"/> may take.</summary>
[Flags]
internal enum JsonKinds
{
    None = 0,
    String = 1,
    Number = 2,
    Integer = 4,
    Boolean = 8,
    Object = 16,
    Array = 32,
    Null = 64,
    Any = String | Number | Integer | Boolean | Object | Array | Null,
}
