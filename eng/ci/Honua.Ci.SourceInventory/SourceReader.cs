using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Honua.Ci.SourceInventory;

internal static class SourceReader
{
    internal static (MethodEntry[] Methods, string[] Diagnostics) Read(CSharpCompilation compilation)
    {
        var diagnostics = compilation.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error)
            .Select(value => "compiler-error:" + value.Id + ":" + value.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var result = new List<MethodEntry>();
        foreach (var type in Types(compilation.Assembly.GlobalNamespace).Where(type => !type.IsAbstract))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current is not null; current = current.BaseType)
            {
                foreach (var method in current.GetMembers().OfType<IMethodSymbol>().Where(value => value.MethodKind == MethodKind.Ordinary && value.DeclaredAccessibility == Accessibility.Public))
                {
                    var signature = Signature(method);
                    if (!seen.Add(signature)) continue;
                    var effective = Inventory.Merge(Attributes(type, "class").Concat(Attributes(method, "method")));
                    if (!method.GetAttributes().Any(value => Inventory.IsFact(value.AttributeClass)) &&
                        !effective.Any(value => value.Scope == "method" && IsFactName(compilation, value.Name))) continue;
                    if (type.IsGenericType || method.ReturnsByRef || method.ReturnsByRefReadonly)
                        diagnostics.Add("unsupported-generic-test-class-or-ref-return:" + Inventory.TypeName(type) + "." + signature);
                    foreach (var attribute in effective)
                    {
                        var symbol = compilation.GetTypeByMetadataName(attribute.Name);
                        if (symbol is not null && HasTraitDiscoverer(symbol))
                            diagnostics.Add("unsupported-executable-trait-discoverer:" + attribute.Name);
                        if (symbol is not null && Inventory.IsFact(symbol) && attribute.Name is not "Xunit.FactAttribute" and not "Xunit.TheoryAttribute")
                            diagnostics.Add("unsupported-custom-fact-runtime-semantics:" + attribute.Name);
                    }
                    var declared = Inventory.Sort(type.GetAttributes().Where(value => value.AttributeClass is not null && Inventory.Relevant(value.AttributeClass))
                        .Select(value => Entry(value, "class", false)).Concat(method.GetAttributes()
                        .Where(value => value.AttributeClass is not null && Inventory.Relevant(value.AttributeClass)).Select(value => Entry(value, "method", false))));
                    result.Add(new(Inventory.TypeName(type), Inventory.TypeName(method.ContainingType), signature,
                        declared, effective, Inventory.Coverage(effective)));
                }
            }
        }
        return (result.OrderBy(value => value.TestClass, StringComparer.Ordinal).ThenBy(value => value.Signature, StringComparer.Ordinal).ToArray(),
            diagnostics.Distinct().Order(StringComparer.Ordinal).ToArray());
    }
    private static bool IsFactName(CSharpCompilation compilation, string name) => Inventory.IsFact(compilation.GetTypeByMetadataName(name));
    private static bool HasTraitDiscoverer(INamedTypeSymbol type)
        => type.GetAttributes().Any(value => value.AttributeClass?.ToDisplayString() == "Xunit.Sdk.TraitDiscovererAttribute") ||
           type.BaseType is not null && HasTraitDiscoverer(type.BaseType);
    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol scope)
    {
        foreach (var member in scope.GetMembers())
        {
            if (member is INamedTypeSymbol type) { yield return type; foreach (var nested in Types(type)) yield return nested; }
            else if (member is INamespaceSymbol space) foreach (var child in Types(space)) yield return child;
        }
    }
    private static string Signature(IMethodSymbol method)
        => Inventory.TypeName(method.ReturnType) + " " + method.Name + "``" + method.Arity + "(" + string.Join(",", method.Parameters.Select(value =>
            (value.RefKind == RefKind.None ? "" : value.RefKind.ToString().ToLowerInvariant() + " ") + Inventory.TypeName(value.Type))) + ")";
    private static AttributeEntry Entry(AttributeData value, string scope, bool inherited)
        => new(Inventory.TypeName(value.AttributeClass!), scope, inherited, value.ConstructorArguments.Select(Inventory.Constant).ToArray(),
            new(value.NamedArguments.ToDictionary(pair => pair.Key, pair => Inventory.Constant(pair.Value)), StringComparer.Ordinal));
    private static IEnumerable<(AttributeEntry Entry, bool Inherited, bool Multiple)> Attributes(ISymbol symbol, string scope)
    {
        var inherited = false;
        while (symbol is not null)
        {
            foreach (var value in symbol.GetAttributes().Where(value => value.AttributeClass is not null && Inventory.Relevant(value.AttributeClass)))
            {
                var attributeType = value.AttributeClass!;
                AttributeData? usage = null;
                for (var type = attributeType; type is not null && usage is null; type = type.BaseType)
                    usage = type.GetAttributes().FirstOrDefault(value => value.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");
                var policy = Inventory.Usage(usage?.NamedArguments.Select(pair => (pair.Key, Inventory.Constant(pair.Value))) ?? []);
                yield return (Entry(value, scope, inherited), policy.Inherited, policy.Multiple);
            }
            symbol = symbol is INamedTypeSymbol named ? named.BaseType! : symbol is IMethodSymbol method ? method.OverriddenMethod! : null!;
            inherited = true;
        }
    }
}
