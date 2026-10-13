using System.Reflection;
using System.Runtime.Loader;

namespace Honua.Ci.SourceInventory;

internal static class ReflectionReader
{
    internal static (MethodEntry[] Methods, string[] Diagnostics) Read(Assembly assembly)
    {
        var result = new List<MethodEntry>();
        var diagnostics = new List<string>();
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error)
        {
            return ([], ["reflection-type-load-failed:" + string.Join(";", error.LoaderExceptions.Select(value => value?.Message))]);
        }
        foreach (var type in types.Where(value => !value.IsAbstract))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy).Where(value => !value.IsSpecialName))
            {
                var effective = Inventory.Merge(Attributes(type, "class").Concat(Attributes(method, "method")));
                var attributeTypes = MethodAttributeTypes(method).Concat(ClassAttributeTypes(type))
                    .Distinct().ToDictionary(Inventory.TypeName, value => value, StringComparer.Ordinal);
                if (!effective.Any(value => value.Scope == "method" && attributeTypes.TryGetValue(value.Name, out var attributeType) && Inventory.IsFact(attributeType))) continue;
                if (type.IsGenericType || method.ReturnType.IsByRef)
                    diagnostics.Add("unsupported-generic-test-class-or-ref-return:" + Inventory.TypeName(type) + "." + Signature(method));
                foreach (var attribute in effective.Select(value => attributeTypes[value.Name]).Distinct())
                {
                    if (HasTraitDiscoverer(attribute)) diagnostics.Add("unsupported-executable-trait-discoverer:" + Inventory.TypeName(attribute));
                    if (Inventory.IsFact(attribute) && attribute.FullName is not "Xunit.FactAttribute" and not "Xunit.TheoryAttribute")
                        diagnostics.Add("unsupported-custom-fact-runtime-semantics:" + Inventory.TypeName(attribute));
                }
                var declared = Inventory.Sort(type.GetCustomAttributesData().Where(value => Inventory.Relevant(value.AttributeType)).Select(value => Entry(value, "class", false))
                    .Concat(method.GetCustomAttributesData().Where(value => Inventory.Relevant(value.AttributeType)).Select(value => Entry(value, "method", false))));
                result.Add(new(Inventory.TypeName(type), Inventory.TypeName(method.DeclaringType!), Signature(method), declared, effective, Inventory.Coverage(effective)));
            }
        return (result.OrderBy(value => value.TestClass, StringComparer.Ordinal).ThenBy(value => value.Signature, StringComparer.Ordinal).ToArray(),
            diagnostics.Distinct().Order(StringComparer.Ordinal).ToArray());
    }
    private static bool HasTraitDiscoverer(Type type)
        => type.GetCustomAttributesData().Any(value => value.AttributeType.FullName == "Xunit.Sdk.TraitDiscovererAttribute") ||
           type.BaseType is not null && HasTraitDiscoverer(type.BaseType);
    private static IEnumerable<Type> MethodAttributeTypes(MethodInfo method)
    {
        foreach (var value in method.GetCustomAttributesData()) yield return value.AttributeType;
        var parent = Parent(method);
        if (parent is not null) foreach (var value in MethodAttributeTypes(parent)) yield return value;
    }
    private static IEnumerable<Type> ClassAttributeTypes(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var value in current.GetCustomAttributesData()) yield return value.AttributeType;
    }
    private static MethodInfo? Parent(MethodInfo method)
    {
        var definition = method.GetBaseDefinition();
        if (definition == method) return null;
        for (var type = method.DeclaringType?.BaseType; type is not null; type = type.BaseType)
        {
            var parent = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(value => value.GetBaseDefinition() == definition);
            if (parent is not null) return parent;
        }
        return null;
    }
    private static string Signature(MethodInfo method)
        => Inventory.TypeName(method.ReturnType) + " " + method.Name + "``" + method.GetGenericArguments().Length + "(" +
           string.Join(",", method.GetParameters().Select(value => (value.ParameterType.IsByRef ? value.IsOut ? "out " : value.IsIn ? "in " : "ref " : "") + Inventory.TypeName(value.ParameterType))) + ")";
    private static AttributeEntry Entry(CustomAttributeData value, string scope, bool inherited)
        => new(Inventory.TypeName(value.AttributeType), scope, inherited, value.ConstructorArguments.Select(Inventory.Constant).ToArray(),
            new(value.NamedArguments.ToDictionary(pair => pair.MemberName, pair => Inventory.Constant(pair.TypedValue)), StringComparer.Ordinal));
    private static IEnumerable<(AttributeEntry Entry, bool Inherited, bool Multiple)> Attributes(MemberInfo symbol, string scope)
    {
        var inherited = false;
        while (symbol is not null)
        {
            foreach (var value in symbol.GetCustomAttributesData().Where(value => Inventory.Relevant(value.AttributeType)))
            {
                CustomAttributeData? usage = null;
                for (var type = value.AttributeType; type is not null && usage is null; type = type.BaseType)
                    usage = type.GetCustomAttributesData().FirstOrDefault(value => value.AttributeType.FullName == "System.AttributeUsageAttribute");
                var policy = Inventory.Usage(usage?.NamedArguments.Select(pair => (pair.MemberName, Inventory.Constant(pair.TypedValue))) ?? []);
                yield return (Entry(value, scope, inherited), policy.Inherited, policy.Multiple);
            }
            symbol = symbol is Type typeSymbol ? typeSymbol.BaseType! : symbol is MethodInfo method ? Parent(method)! : null!;
            inherited = true;
        }
    }
    internal sealed class ExistingOutputContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var file = Path.Join(directory, assemblyName.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
}
