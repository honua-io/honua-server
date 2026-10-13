using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Honua.Ci.SourceInventory;

internal sealed record AttributeEntry(string Name, string Scope, bool Inherited, object?[] Arguments,
    SortedDictionary<string, object?> NamedArguments);
internal sealed record MethodEntry(string TestClass, string DeclaringClass, string Signature,
    AttributeEntry[] DeclaredAttributes, AttributeEntry[] EffectiveAttributes,
    SortedDictionary<string, string[]> CoverageValues);
internal sealed record InputEntry(string Path, string Sha256);
internal sealed record InventoryDocument(string Schema, string Project, SortedDictionary<string, string> Configuration,
    InputEntry[] Sources, InputEntry[] References, MethodEntry[] Methods, string[] Diagnostics);

internal static class Inventory
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal const string Schema = "honua.ci.semantic-test-inventory/v1";
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    internal static bool IsFact(INamedTypeSymbol? type)
        => type is not null && (type.ToDisplayString() is "Xunit.FactAttribute" or "Xunit.TheoryAttribute" || IsFact(type.BaseType));
    internal static bool IsFact(Type? type)
        => type is not null && (type.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute" || IsFact(type.BaseType));
    internal static bool Relevant(INamedTypeSymbol type)
        => IsFact(type) || type.ContainingNamespace.ToDisplayString() == "Xunit" ||
           type.ContainingNamespace.ToDisplayString() == "Honua.TestKit.Attributes" ||
           type.AllInterfaces.Any(value => value.ToDisplayString() == "Xunit.Sdk.ITraitAttribute");
    internal static bool Relevant(Type type)
        => IsFact(type) || type.Namespace is "Xunit" or "Honua.TestKit.Attributes" ||
           type.GetInterfaces().Any(value => value.FullName == "Xunit.Sdk.ITraitAttribute");

    internal static string TypeName(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => TypeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
        IPointerTypeSymbol pointer => TypeName(pointer.PointedAtType) + "*",
        ITypeParameterSymbol parameter => (parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!") + parameter.Ordinal.ToString(CultureInfo.InvariantCulture),
        INamedTypeSymbol named => (named.ContainingType is null
            ? (named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace + ".")
            : TypeName(named.ContainingType) + ".") + named.Name +
            (named.TypeArguments.Length == 0 ? "" : "<" + string.Join(",", named.TypeArguments.Select(TypeName)) + ">"),
        _ => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal)
    };
    internal static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!);
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        var name = (type.FullName ?? type.Name).Split('`')[0].Replace('+', '.');
        return name + (type.IsGenericType ? "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">" : "");
    }
    internal static object? Constant(TypedConstant value) => value.Kind switch
    {
        TypedConstantKind.Array => value.IsNull ? null : value.Values.Select(Constant).ToArray(),
        TypedConstantKind.Type => value.Value is ITypeSymbol type ? TypeName(type) : null,
        _ => value.Value
    };
    internal static object? Constant(CustomAttributeTypedArgument value)
        => value.Value is IReadOnlyCollection<CustomAttributeTypedArgument> array ? array.Select(Constant).ToArray()
            : value.Value is Type type ? TypeName(type) : value.Value;

    internal static AttributeEntry[] Sort(IEnumerable<AttributeEntry> attributes)
        => attributes.OrderBy(Serialize, StringComparer.Ordinal).ToArray();
    internal static SortedDictionary<string, string[]> Coverage(IEnumerable<AttributeEntry> attributes)
    {
        var values = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string key, object? value)
        {
            if (!values.TryGetValue(key, out var set)) { set = new(StringComparer.Ordinal); values.Add(key, set); }
            if (value is object?[] array) { foreach (var item in array) Add(key, item); }
            else if (value is string text) set.Add(text);
        }
        foreach (var attribute in attributes)
        {
            if (attribute.Name == "Xunit.TraitAttribute" && attribute.Arguments.Length == 2 && attribute.Arguments[0] is string key)
                Add(key, attribute.Arguments[1]);
            foreach (var (name, coverageKey) in new[] { ("Protocol", "Protocol"), ("Operation", "Operation"), ("Endpoint", "Endpoint") })
                if (attribute.Name == $"Honua.TestKit.Attributes.{name}Attribute")
                    foreach (var argument in attribute.Arguments) Add(coverageKey, argument);
        }
        return new(values.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray()), StringComparer.Ordinal);
    }
    internal static string[] Unsupported(IEnumerable<AttributeEntry> attributes)
        => attributes.Where(value => value.Name is not "Xunit.FactAttribute" and not "Xunit.TheoryAttribute" &&
                !value.Name.StartsWith("Honua.TestKit.Attributes.", StringComparison.Ordinal) &&
                value.Name.EndsWith("FactAttribute", StringComparison.Ordinal))
            .Select(value => "unsupported-custom-fact-runtime-semantics:" + value.Name).Distinct().Order(StringComparer.Ordinal).ToArray();

    internal static (bool Inherited, bool Multiple) Usage(IEnumerable<(string Name, object? Value)> named)
    {
        var entries = named.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return (!entries.TryGetValue("Inherited", out var inherited) || inherited is true,
            entries.TryGetValue("AllowMultiple", out var multiple) && multiple is true);
    }
    internal static AttributeEntry[] Merge(IEnumerable<(AttributeEntry Entry, bool Inherited, bool Multiple)> attributes)
    {
        var result = new List<AttributeEntry>();
        foreach (var (entry, inherited, multiple) in attributes)
            if (!entry.Inherited || inherited && (multiple || !result.Any(value => value.Name == entry.Name && value.Scope == entry.Scope))) result.Add(entry);
        return Sort(result);
    }
}
