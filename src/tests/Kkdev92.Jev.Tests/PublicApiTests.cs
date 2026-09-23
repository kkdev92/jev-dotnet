using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// Snapshots the public API surface so a change to it has to be deliberate.
/// </summary>
/// <remarks>
/// <para>
/// Package validation compares against a published baseline, and there is not one yet —
/// 0.1.0-alpha is the first release — so this stands in: a type or member that appears,
/// disappears or changes shape shows up as a diff in <c>src/tests/PublicApi/{assembly}.approved.txt</c>
/// that a reviewer has to accept. Once a version is on nuget.org,
/// <c>PackageValidationBaselineVersion</c> is what decides binary compatibility, and this becomes
/// the cheaper first signal rather than the answer.
/// </para>
/// <para>
/// What it records: each type's kind and modifiers, its base type, interfaces and generic
/// constraints, and its public and protected members — property types with their nullability and
/// whether the setter is <c>init</c>, method and operator signatures with ref kinds and default
/// values, enum members and constants with their values, and the handful of attributes that change
/// what a caller may do (<c>[Obsolete]</c>, <c>[Experimental]</c>, <c>[RequiresUnreferencedCode]</c>,
/// <c>[RequiresDynamicCode]</c>, <c>[EditorBrowsable(Never)]</c>, <c>[Flags]</c>).
/// </para>
/// <para>
/// What it does not record: other attributes, the <c>notnull</c> constraint and a <c>T?</c> on a
/// type parameter (both nullable annotations rather than anything reflection states plainly),
/// <c>scoped</c>, whether a type is a record, and anything about behaviour. "The surface has not
/// moved" means the shapes above have not moved, and nothing stronger than that.
/// </para>
/// <para>
/// It also keeps the generated wire types where they belong. They are internal by design — the
/// public types are hand-written over them — and an emitter change that made them public would
/// show up here as sixteen new types rather than as a surprise in a consumer's IntelliSense.
/// </para>
/// </remarks>
public sealed class PublicApiTests
{
    /// <summary>
    /// Set <c>APPROVE_PUBLIC_API=1</c> to rewrite the approved files instead of asserting.
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a constant, so approving a deliberate API change does
    /// not require editing and reverting test code:
    /// <c>APPROVE_PUBLIC_API=1 dotnet test --project src/tests/Kkdev92.Jev.Tests --
    /// --filter-class Kkdev92.Jev.Tests.PublicApiTests</c>, then review the diff.
    /// </remarks>
    private static bool OverwriteApprovedFile
        => Environment.GetEnvironmentVariable("APPROVE_PUBLIC_API") == "1";

    [Theory]
    [InlineData("Kkdev92.Jev")]
    [InlineData("Kkdev92.Jev.DependencyInjection")]
    public void PublicApiMatchesTheApprovedSurface(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var actual = DescribePublicApi(assembly);

        var approvedPath = Path.Combine(RepositoryRoot.Value, "src", "tests", "PublicApi", $"{assemblyName}.approved.txt");

        if (OverwriteApprovedFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(approvedPath)!);
            File.WriteAllText(approvedPath, actual, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(approvedPath), $"No approved API file at '{approvedPath}'.");

        var approved = File.ReadAllText(approvedPath, new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

        // A diff here is not automatically wrong. It means the public surface moved, and the
        // reviewer has to agree that it should have.
        Assert.Equal(approved, actual);
    }

    /// <summary>The generated wire types never reach the public surface.</summary>
    [Fact]
    public void NoWireTypeIsPublic()
    {
        var exported = typeof(JevClient).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, t => t.Namespace?.StartsWith("Kkdev92.Jev.Wire", StringComparison.Ordinal) == true);
    }

    /// <summary>Renders the public surface in a stable, diff-friendly form.</summary>
    private static string DescribePublicApi(Assembly assembly)
    {
        var builder = new StringBuilder();
        var nullability = new NullabilityInfoContext();

        var types = assembly.GetExportedTypes()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            builder.Append(Describe(type)).Append('\n');

            var members = type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(IsVisible)
                .Where(IsInteresting)
                .Select(member => Describe(member, nullability))
                .OrderBy(m => m, StringComparer.Ordinal);

            foreach (var member in members)
            {
                builder.Append("    ").Append(member).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Public, or protected on a type a consumer can derive from — both are surface.</summary>
    private static bool IsVisible(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || ((method.IsFamily || method.IsFamilyOrAssembly) && !member.DeclaringType!.IsSealed),
        FieldInfo field => field.IsPublic || ((field.IsFamily || field.IsFamilyOrAssembly) && !member.DeclaringType!.IsSealed),
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(a => IsVisible(a)),
        EventInfo @event => @event.AddMethod is { } add && IsVisible(add),
        Type nested => nested.IsNestedPublic || ((nested.IsNestedFamily || nested.IsNestedFamORAssem) && !member.DeclaringType!.IsSealed),
        _ => false,
    };

    private static bool IsInteresting(MemberInfo member)
    {
        // Property accessors and event add/remove appear separately as methods; the property or
        // event line already covers them. Operators are special names too, and are kept.
        if (member is MethodInfo { IsSpecialName: true } method && !method.Name.StartsWith("op_", StringComparison.Ordinal))
        {
            return false;
        }

        // An enum's backing field is an implementation detail of every enum.
        if (member is FieldInfo { IsSpecialName: true })
        {
            return false;
        }

        // Object's members are noise on every single type.
        return member.DeclaringType != typeof(object);
    }

    private static string Describe(Type type)
    {
        var kind = type switch
        {
            { IsEnum: true } => $"enum {type.FullName} : {TypeName(Enum.GetUnderlyingType(type))}",
            { IsInterface: true } => $"interface {DefinitionName(type)}",
            { IsValueType: true } => $"{(IsReadOnly(type) ? "readonly " : string.Empty)}{(type.IsByRefLike ? "ref " : string.Empty)}struct {DefinitionName(type)}",
            { IsAbstract: true, IsSealed: true } => $"static class {DefinitionName(type)}",
            { IsAbstract: true } => $"abstract class {DefinitionName(type)}",
            { IsSealed: true } => $"sealed class {DefinitionName(type)}",
            _ => $"class {DefinitionName(type)}",
        };

        var bases = new List<string>();

        if (type is { IsClass: true, BaseType: { } baseType } && baseType != typeof(object))
        {
            bases.Add(TypeName(baseType));
        }

        if (!type.IsEnum)
        {
            bases.AddRange(type.GetInterfaces().Select(i => TypeName(i)).Order(StringComparer.Ordinal));
        }

        var text = Attributes(type) + kind;

        if (bases.Count > 0)
        {
            text += " : " + string.Join(", ", bases);
        }

        if (type.IsGenericTypeDefinition)
        {
            text += Constraints(type.GetGenericArguments());
        }

        return text;
    }

    private static string Describe(MemberInfo member, NullabilityInfoContext nullability) => member switch
    {
        PropertyInfo property => Attributes(property) + DescribeProperty(property, nullability),

        FieldInfo { IsLiteral: true, DeclaringType.IsEnum: true } field =>
            $"{field.Name} = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)}",

        FieldInfo field => Attributes(field) + (field.IsLiteral
            ? $"const {TypeName(field.FieldType, nullability.Create(field))} {field.Name} = {Literal(field.GetRawConstantValue())}"
            : $"{Access(field.IsPublic)}{(field.IsStatic ? "static " : string.Empty)}{(field.IsInitOnly ? "readonly " : string.Empty)}{TypeName(field.FieldType, nullability.Create(field))} {field.Name}"),

        ConstructorInfo constructor =>
            Attributes(constructor) + $"{Access(constructor.IsPublic)}.ctor({Parameters(constructor, nullability)})",

        MethodInfo method => Attributes(method) + DescribeMethod(method, nullability),

        EventInfo @event => Attributes(@event) + $"event {TypeName(@event.EventHandlerType!)} {@event.Name}",

        Type nested => Describe(nested),

        _ => member.ToString() ?? member.Name,
    };

    private static string DescribeProperty(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var accessors = new StringBuilder();

        if (property.GetMethod is { } getter && IsVisible(getter))
        {
            accessors.Append(Access(getter.IsPublic)).Append("get; ");
        }

        if (property.SetMethod is { } setter && IsVisible(setter))
        {
            var isInit = setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
            accessors.Append(Access(setter.IsPublic)).Append(isInit ? "init; " : "set; ");
        }

        var accessor = property.GetAccessors(nonPublic: true)[0];
        var modifiers = accessor.IsStatic ? "static " : string.Empty;
        var indexer = property.GetIndexParameters() is { Length: > 0 } parameters
            ? $"[{string.Join(", ", parameters.Select(p => DescribeParameter(p, nullability)))}]"
            : string.Empty;

        return $"{modifiers}{TypeName(property.PropertyType, nullability.Create(property))} {property.Name}{indexer} {{ {accessors}}}";
    }

    private static string DescribeMethod(MethodInfo method, NullabilityInfoContext nullability)
    {
        var modifiers = new StringBuilder(Access(method.IsPublic));

        if (method.IsStatic)
        {
            modifiers.Append("static ");
        }
        else if (method.IsAbstract && !method.DeclaringType!.IsInterface)
        {
            modifiers.Append("abstract ");
        }
        else if (method.IsVirtual && !method.IsFinal && !method.DeclaringType!.IsInterface)
        {
            modifiers.Append(method.GetBaseDefinition().DeclaringType == method.DeclaringType ? "virtual " : "override ");
        }

        var name = method.IsGenericMethodDefinition
            ? $"{method.Name}<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>"
            : method.Name;

        var constraints = method.IsGenericMethodDefinition ? Constraints(method.GetGenericArguments()) : string.Empty;

        return $"{modifiers}{TypeName(method.ReturnType, nullability.Create(method.ReturnParameter))} {name}({Parameters(method, nullability)}){constraints}";
    }

    private static string Parameters(MethodBase method, NullabilityInfoContext nullability)
    {
        var isExtension = method.IsDefined(typeof(ExtensionAttribute), inherit: false);

        return string.Join(", ", method.GetParameters().Select((p, i) => (isExtension && i == 0 ? "this " : string.Empty) + DescribeParameter(p, nullability)));
    }

    private static string DescribeParameter(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var type = parameter.ParameterType;
        var prefix = string.Empty;

        if (type.IsByRef)
        {
            prefix = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
            type = type.GetElementType()!;
        }

        if (parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false)
            || parameter.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ParamCollectionAttribute"))
        {
            prefix = "params " + prefix;
        }

        var text = $"{prefix}{TypeName(type, nullability.Create(parameter))} {parameter.Name}";

        if (!parameter.HasDefaultValue)
        {
            return text;
        }

        // Metadata stores `default` for a struct as a null constant, which would read as a
        // nullable parameter defaulting to null.
        var value = parameter.RawDefaultValue;
        var isDefaultStruct = value is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null;

        return $"{text} = {(isDefaultStruct ? "default" : Literal(value))}";
    }

    private static string Constraints(Type[] parameters)
    {
        var clauses = new StringBuilder();

        foreach (var parameter in parameters)
        {
            var parts = new List<string>();
            var attributes = parameter.GenericParameterAttributes;
            var isStruct = attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint);

            if (isStruct)
            {
                parts.Add("struct");
            }
            else if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add("class");
            }

            parts.AddRange(parameter.GetGenericParameterConstraints()
                .Where(c => c != typeof(ValueType))
                .Select(c => TypeName(c))
                .Order(StringComparer.Ordinal));

            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !isStruct)
            {
                parts.Add("new()");
            }

            if (attributes.HasFlag(GenericParameterAttributes.AllowByRefLike))
            {
                parts.Add("allows ref struct");
            }

            if (parts.Count > 0)
            {
                clauses.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", parts));
            }
        }

        return clauses.ToString();
    }

    /// <summary>The attributes that change what a caller may do with a member, and nothing else.</summary>
    private static string Attributes(MemberInfo member)
    {
        var rendered = new List<string>();

        foreach (var attribute in member.CustomAttributes)
        {
            switch (attribute.AttributeType.FullName)
            {
                case "System.ObsoleteAttribute":
                    rendered.Add("[Obsolete]");
                    break;
                case "System.Diagnostics.CodeAnalysis.ExperimentalAttribute":
                    rendered.Add($"[Experimental({Literal(attribute.ConstructorArguments[0].Value)})]");
                    break;
                case "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute":
                    rendered.Add("[RequiresUnreferencedCode]");
                    break;
                case "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute":
                    rendered.Add("[RequiresDynamicCode]");
                    break;
                case "System.ComponentModel.EditorBrowsableAttribute"
                    when attribute.ConstructorArguments is [{ Value: int state }] && state == (int)EditorBrowsableState.Never:
                    rendered.Add("[EditorBrowsable(Never)]");
                    break;
                case "System.FlagsAttribute":
                    rendered.Add("[Flags]");
                    break;
            }
        }

        rendered.Sort(StringComparer.Ordinal);

        return rendered.Count == 0 ? string.Empty : string.Concat(rendered) + " ";
    }

    private static string Access(bool isPublic) => isPublic ? string.Empty : "protected ";

    private static bool IsReadOnly(Type type)
        => type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");

    /// <summary>A type's own name with its type parameters, as it is declared.</summary>
    private static string DefinitionName(Type type)
    {
        if (!type.IsGenericTypeDefinition)
        {
            return type.FullName!;
        }

        return $"{StripArity(type.FullName!)}<{string.Join(", ", type.GetGenericArguments().Select(a => a.Name))}>";
    }

    /// <summary>Renders a type name without assembly qualification, so the output is stable.</summary>
    /// <remarks>
    /// Nullability is rendered where the metadata states it: <c>string?</c> against <c>string</c>
    /// is a contract change for every caller with nullable reference types enabled, even though
    /// it is not a binary one. Not for a type parameter, though: <see cref="NullabilityInfoContext"/>
    /// reports an unconstrained <c>T</c> as nullable because it may be instantiated with a nullable
    /// type, which would render every <c>T</c> as <c>T?</c> and hide the difference that matters.
    /// </remarks>
    private static string TypeName(Type type, NullabilityInfo? nullability = null)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return TypeName(underlying, nullability?.GenericTypeArguments is [var inner] ? inner : null) + "?";
        }

        string name;

        if (type.IsArray)
        {
            name = $"{TypeName(type.GetElementType()!, nullability?.ElementType)}[]";
        }
        else if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var argumentNullability = nullability?.GenericTypeArguments;
            var rendered = arguments.Select((a, i) => TypeName(a, argumentNullability is { } n && i < n.Length ? n[i] : null));

            name = $"{StripArity(type.GetGenericTypeDefinition().FullName!)}<{string.Join(", ", rendered)}>";
        }
        else
        {
            name = type.FullName ?? type.Name;
        }

        return !type.IsValueType && nullability is { ReadState: NullabilityState.Nullable } ? name + "?" : name;
    }

    private static string StripArity(string fullName)
    {
        var builder = new StringBuilder(fullName.Length);

        for (var i = 0; i < fullName.Length; i++)
        {
            if (fullName[i] == '`')
            {
                while (i + 1 < fullName.Length && char.IsAsciiDigit(fullName[i + 1]))
                {
                    i++;
                }

                continue;
            }

            builder.Append(fullName[i]);
        }

        return builder.ToString();
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        char character => $"'{character}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
