using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kkdev92.Jev.CodeGen.Commands;

/// <summary>
/// Removes documentation for members a consumer cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The compiler documents whatever carries a <c>///</c> comment, private fields included, and this
/// repository comments private members on purpose — the reasoning belongs next to the code it
/// explains. That reasoning then ships inside the package. Microsoft's own guidance on the switch
/// says documenting private members "exposes the inner (potentially confidential) workings of your
/// library", and there is no compiler option for it: <c>-doc</c> takes a file, not a visibility.
/// </para>
/// <para>
/// Visibility comes from the compiled assembly rather than from a guess about the XML: a member id
/// says nothing about whether anyone outside can reach it. The assembly is read as metadata and
/// never loaded, so this cannot run its code or fail on a reference it has no copy of.
/// </para>
/// <para>
/// Whole <c>&lt;member&gt;</c> elements are removed and nothing is edited inside one.
/// <c>&lt;inheritdoc/&gt;</c> on a public member resolves against base types in this same file, so
/// trimming inside an element could break a reference the surviving documentation depends on.
/// </para>
/// </remarks>
internal static partial class FilterDocsCommand
{
    public static int Run(string documentationFile, string assemblyFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentationFile);
        ArgumentException.ThrowIfNullOrEmpty(assemblyFile);

        if (!File.Exists(documentationFile))
        {
            Console.Error.WriteLine($"codegen: no documentation file at '{documentationFile}'.");
            return 1;
        }

        if (!File.Exists(assemblyFile))
        {
            Console.Error.WriteLine($"codegen: no assembly at '{assemblyFile}'.");
            return 1;
        }

        var visible = ReadVisibleMembers(assemblyFile);
        var document = XDocument.Load(documentationFile);
        var members = document.Root?.Element("members");

        if (members is null)
        {
            return 0;
        }

        var removed = 0;

        foreach (var member in members.Elements("member").ToList())
        {
            if ((string?)member.Attribute("name") is not { } id)
            {
                continue;
            }

            if (IsVisible(id, visible))
            {
                continue;
            }

            member.Remove();
            removed++;
        }

        if (removed > 0)
        {
            document.Save(documentationFile);
        }

        Console.WriteLine(
            $"filter-docs        : {removed} of {removed + members.Elements("member").Count()} entries removed "
            + $"from {Path.GetFileName(documentationFile)}");

        return 0;
    }

    /// <summary>
    /// Whether a documentation id names something a consumer can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A documentation id carries things the member table does not: a signature
    /// (<c>M:T.M(System.String)</c> against <c>M:T.M</c>), a conversion operator's return type
    /// after <c>~</c>, and an interface spelled with <c>#</c>. Those are cut here. Overloads share
    /// a visibility in every case this contract produces, and a member the assembly does not admit
    /// to at all is removed whatever its signature says.
    /// </para>
    /// <para>
    /// Arity is cut from <em>both</em> sides rather than compared. A generic method's id is
    /// <c>Get``1</c>, a generic type's is <c>ChoiceAnswer`1</c>, and the table spells the type
    /// the same way but the method without it. Comparing literally would make every public generic
    /// method look like one the assembly does not declare, and remove its documentation — a
    /// failure that points the wrong way and is invisible in the package: hovering over the method
    /// simply shows nothing.
    /// </para>
    /// </remarks>
    private static bool IsVisible(string id, IReadOnlySet<string> visible)
    {
        var withoutSignature = id.IndexOf('(', StringComparison.Ordinal) is var open and >= 0
            ? id[..open]
            : id;

        // An explicit interface implementation spells the interface with '#', and a generic
        // parameter list with '{' '}'.
        var normalized = withoutSignature.Replace('#', '.');

        if (normalized.IndexOf('{', StringComparison.Ordinal) is var brace and >= 0)
        {
            normalized = normalized[..brace];
        }

        if (normalized.IndexOf('~', StringComparison.Ordinal) is var tilde and >= 0)
        {
            normalized = normalized[..tilde];
        }

        return visible.Contains(StripArity(normalized));
    }

    /// <summary>
    /// Records a member the same way an id will be looked up: without its arity.
    /// </summary>
    /// <remarks>
    /// Both sides go through this. A generic type is <c>Owner`1</c> in metadata and in the id, and
    /// a generic method carries its arity only in the id — so normalizing one side and not the
    /// other trades one mismatch for another.
    /// </remarks>
    private static void Add(HashSet<string> visible, string member) => visible.Add(StripArity(member));

    /// <summary>Removes the <c>`n</c> and <c>``n</c> arity markers wherever they appear.</summary>
    /// <remarks>
    /// Wherever, not at the first one: a member of a generic type is
    /// <c>M:Ns.Owner`1.Method``1</c>, and cutting at the first backtick would throw away the
    /// method and leave the owner — which would keep documentation for anything the owner
    /// declares, public or not.
    /// </remarks>
    private static string StripArity(string id) => Arity().Replace(id, string.Empty);

    [GeneratedRegex("``?[0-9]+")]
    private static partial Regex Arity();

    private static IReadOnlySet<string> ReadVisibleMembers(string assemblyFile)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyFile);
        using var peReader = new PEReader(stream);

        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);

            if (!IsTypeVisible(metadata, definition))
            {
                continue;
            }

            var typeName = XmlTypeName(metadata, definition);
            Add(visible, "T:" + typeName);

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);

                if (IsAccessible(method.Attributes & MethodAttributes.MemberAccessMask))
                {
                    Add(visible, "M:" + typeName + "." + metadata.GetString(method.Name).Replace('.', '#'));
                    Add(visible, "M:" + typeName + "." + metadata.GetString(method.Name));
                }
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();

                if (IsAccessorVisible(metadata, accessors.Getter) || IsAccessorVisible(metadata, accessors.Setter))
                {
                    Add(visible, "P:" + typeName + "." + metadata.GetString(property.Name));
                }
            }

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);

                if (IsAccessible(field.Attributes & FieldAttributes.FieldAccessMask))
                {
                    Add(visible, "F:" + typeName + "." + metadata.GetString(field.Name));
                }
            }

            foreach (var eventHandle in definition.GetEvents())
            {
                var declaration = metadata.GetEventDefinition(eventHandle);

                if (IsAccessorVisible(metadata, declaration.GetAccessors().Adder))
                {
                    Add(visible, "E:" + typeName + "." + metadata.GetString(declaration.Name));
                }
            }
        }

        return visible;
    }

    /// <summary>Protected counts as visible: a consumer can derive.</summary>
    private static bool IsAccessible(MethodAttributes access)
        => access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static bool IsAccessible(FieldAttributes access)
        => access is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;

    private static bool IsAccessorVisible(MetadataReader metadata, MethodDefinitionHandle handle)
        => !handle.IsNil
            && IsAccessible(metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask);

    private static bool IsTypeVisible(MetadataReader metadata, TypeDefinition definition)
    {
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;

        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        if (visibility is not (TypeAttributes.NestedPublic or TypeAttributes.NestedFamily
            or TypeAttributes.NestedFamORAssem))
        {
            return false;
        }

        // A public type nested in an internal one is not reachable, so the whole chain is checked.
        var declaring = definition.GetDeclaringType();

        return !declaring.IsNil && IsTypeVisible(metadata, metadata.GetTypeDefinition(declaring));
    }

    /// <summary>The name a documentation id uses, where nesting is a dot rather than a plus.</summary>
    private static string XmlTypeName(MetadataReader metadata, TypeDefinition definition)
    {
        var name = metadata.GetString(definition.Name);
        var declaringHandle = definition.GetDeclaringType();

        while (!declaringHandle.IsNil)
        {
            var declaring = metadata.GetTypeDefinition(declaringHandle);
            name = metadata.GetString(declaring.Name) + "." + name;
            declaringHandle = declaring.GetDeclaringType();

            if (declaringHandle.IsNil)
            {
                var ns = metadata.GetString(declaring.Namespace);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
        }

        var namespaceName = metadata.GetString(definition.Namespace);

        return string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;
    }
}
