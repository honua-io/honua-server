using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Honua.Ci.SourceInventory;

internal static class PilotFixtures
{
    internal static Comparison Compare(InventoryDocument source, InventoryDocument reflection)
    {
        var diagnostics = source.Diagnostics.Concat(reflection.Diagnostics).Distinct().ToList();
        if (source.Schema != Inventory.Schema || reflection.Schema != Inventory.Schema || source.Project != reflection.Project ||
            Inventory.Serialize(source.Configuration) != Inventory.Serialize(reflection.Configuration) ||
            Inventory.Serialize(source.Sources) != Inventory.Serialize(reflection.Sources) ||
            Inventory.Serialize(source.References) != Inventory.Serialize(reflection.References)) diagnostics.Add("inventory-input-identity-mismatch");
        var sourceMethods = source.Methods.Select(Inventory.Serialize).Order(StringComparer.Ordinal).ToArray();
        var reflectionMethods = reflection.Methods.Select(Inventory.Serialize).Order(StringComparer.Ordinal).ToArray();
        if (!sourceMethods.SequenceEqual(reflectionMethods, StringComparer.Ordinal))
        {
            diagnostics.Add("method-or-attribute-parity-mismatch");
            diagnostics.AddRange(sourceMethods.Except(reflectionMethods, StringComparer.Ordinal).Select(value => "source-only:" + value));
            diagnostics.AddRange(reflectionMethods.Except(sourceMethods, StringComparer.Ordinal).Select(value => "reflection-only:" + value));
        }
        if (source.Methods.Length == 0 || reflection.Methods.Length == 0) diagnostics.Add("empty-test-inventory");
        var metadataMatch = !diagnostics.Any(value => value == "inventory-input-identity-mismatch" ||
            value == "method-or-attribute-parity-mismatch" || value == "empty-test-inventory" ||
            value.StartsWith("compiler-error:", StringComparison.Ordinal) || value.StartsWith("reflection-", StringComparison.Ordinal));
        var runtimeTraitsVerified = !diagnostics.Any(value => value.StartsWith("unsupported-", StringComparison.Ordinal));
        var qualified = metadataMatch && diagnostics.Count == 0;
        return new("honua.ci.semantic-test-parity/v1", qualified ? "exactParity" : "notQualified", qualified, false, metadataMatch, runtimeTraitsVerified,
            source.Methods.Length, reflection.Methods.Length, diagnostics.Order(StringComparer.Ordinal).ToArray());
    }

    internal static int Run()
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: ["PILOT"]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(value => MetadataReference.CreateFromFile(value)).ToArray();
        var sources = new[] { Attributes, First, Second };
        CSharpCompilation Compile(IEnumerable<string> texts, CSharpParseOptions options)
            => CSharpCompilation.Create("InventoryFixture", texts.Select((text, index) => CSharpSyntaxTree.ParseText(text, options, $"fixture-{index}.cs")), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        InventoryDocument Document((MethodEntry[] Methods, string[] Diagnostics) result)
            => new(Inventory.Schema, "synthetic-fixture.csproj", new(StringComparer.Ordinal) { ["DefineConstants"] = "PILOT" }, [], [], result.Methods, result.Diagnostics);
        var compilation = Compile(sources, parse);
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output); // Synthetic fixture ONLY; inspected projects are never emitted.
        if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
        var context = new ReflectionReader.ExistingOutputContext(AppContext.BaseDirectory);
        output.Position = 0;
        var reflection = Document(ReflectionReader.Read(context.LoadFromStream(output)));
        var source = Document(SourceReader.Read(compilation));
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name + "\n" + Inventory.Serialize(Compare(source, reflection)));
            Console.WriteLine("PASS " + name);
        }
        Require(Compare(source, reflection).Qualified, "aliases/inherited attributes/overrides/partial/overloads/generic signature/conditional/custom coverage constants");
        Require(source.Methods.Count(value => value.Signature.Contains(" Work``", StringComparison.Ordinal)) == 2, "overloads retain separate complete signatures");
        Require(source.Methods.Any(value => value.Signature.Contains(" Conditional``", StringComparison.Ordinal)), "evaluated define selects conditional method");
        Require(source.Methods.Any(value => value.EffectiveAttributes.Any(attribute => attribute.Inherited)), "inherited metadata retained explicitly");
        var mutation = Document(SourceReader.Read(Compile(sources.Select(value => value.Replace("scene-pilot", "changed-protocol", StringComparison.Ordinal)), parse)));
        Require(!Compare(mutation, reflection).Qualified, "coverage constant mutation breaks parity");
        Require(!Compare(Document(SourceReader.Read(Compile(sources, parse.WithPreprocessorSymbols(Array.Empty<string>())))), reflection).Qualified, "conditional discovery mutation breaks parity");
        Require(!Compare(source with { Configuration = new(StringComparer.Ordinal) { ["DefineConstants"] = "OTHER" } }, reflection).Qualified, "configuration mismatch fails qualification");
        Require(!Compare(source with { Methods = [] }, reflection with { Methods = [] }).Qualified, "empty inventories cannot qualify");
        var unsupported = Document(SourceReader.Read(Compile(sources.Append(CustomFact), parse)));
        Require(unsupported.Diagnostics.Any(value => value.StartsWith("unsupported-custom-fact", StringComparison.Ordinal)), "executable custom fact semantics explicitly unsupported");
        Require(!Compare(unsupported, unsupported).Qualified, "matching unsupported inventories cannot qualify");
        var unresolved = Document(SourceReader.Read(Compile(sources.Append("class Bad { MissingSymbol field; }"), parse)));
        Require(unresolved.Diagnostics.Any(value => value.StartsWith("compiler-error:", StringComparison.Ordinal)) && !Compare(unresolved, unresolved).Qualified,
            "unresolved semantic input cannot qualify");
        context.Unload();
        return 0;
    }

    private const string Attributes = """
        namespace Xunit {
            [System.AttributeUsage(System.AttributeTargets.Method, Inherited=true)]
            public class FactAttribute : System.Attribute { public string Skip {get;set;} }
            public class TheoryAttribute : FactAttribute {}
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple=true)]
            public sealed class InlineDataAttribute : System.Attribute { public InlineDataAttribute(params object[] values) {} }
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple=true, Inherited=true)]
            public sealed class TraitAttribute : System.Attribute { public TraitAttribute(string key,string value) {} }
        }
        namespace Honua.TestKit.Attributes {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple=true)]
            public sealed class ProtocolAttribute : System.Attribute { public ProtocolAttribute(params string[] values) {} }
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple=true)]
            public sealed class OperationAttribute : System.Attribute { public OperationAttribute(params string[] values) {} }
        }
        """;
    private const string First = """
        using Check = Xunit.TheoryAttribute;
        using Number = System.Int32;
        namespace Fixture {
            [Xunit.Trait("Category","Base")]
            public abstract class Base {
                [Xunit.Fact] public virtual void Inherited() {}
                [Xunit.Fact] public virtual void Override() {}
            }
            public abstract class Middle : Base { [Xunit.Trait("Tier","Middle")] public override void Override() {} }
            [Honua.TestKit.Attributes.Protocol("scene-pilot")]
            public partial class Tests : Middle {
                public override void Override() {}
                [Check, Xunit.InlineData(3)] public void Work(Number value) {}
                [Check, Xunit.InlineData("a")] public void Work(string value) {}
                [Check] public void Generic<T>(T value) {}
        #if PILOT
                [Xunit.Fact(Skip="constant skip is metadata")] public void Conditional() {}
        #else
                [Xunit.Fact] public void OtherConditional() {}
        #endif
            }
        }
        """;
    private const string Second = """
        namespace Fixture {
            [Honua.TestKit.Attributes.Operation("GetMetadata","GetTile")]
            public partial class Tests { [Xunit.Fact] public void Partial() {} }
        }
        """;
    private const string CustomFact = """
        namespace Fixture {
            public sealed class RuntimeFactAttribute : Xunit.FactAttribute { public RuntimeFactAttribute() { Skip=System.Environment.GetEnvironmentVariable("PILOT_SKIP"); } }
            public sealed class RuntimeTests { [RuntimeFact] public void Dynamic() {} }
        }
        """;
}
