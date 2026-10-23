using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Honua.Ci.SourceInventory;

return args.Length == 1 && args[0] == "self-test" ? PilotFixtures.Run() : Run(args);

static int Run(string[] args)
{
    if (args.Length != 5 || args[0] is not ("source" or "reflect" or "compare"))
    {
        Console.Error.WriteLine("source <evaluated.json> <root> <project> <output.json> | reflect <source.json> <assembly.dll> <reserved> <output.json> | compare <source.json> <reflection.json> <reserved> <output.json> | self-test");
        return 2;
    }
    if (args[0] == "source")
    {
        var root = Path.GetFullPath(args[2]);
        using var evaluation = JsonDocument.Parse(File.ReadAllText(args[1]));
        var configuration = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in evaluation.RootElement.GetProperty("Properties").EnumerateObject()) configuration.Add(property.Name, property.Value.GetString() ?? "");
        var items = evaluation.RootElement.GetProperty("Items");
        string[] Paths(string name) => items.GetProperty(name).EnumerateArray().Select(value =>
            Path.GetFullPath(value.TryGetProperty("FullPath", out var fullPath) ? fullPath.GetString()! :
                Path.Join(root, Path.GetDirectoryName(args[3]), value.GetProperty("Identity").GetString()))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var sources = Paths("Compile");
        var references = Paths("ReferencePath");
        var diagnostics = new List<string>();
        var sourceInputs = sources.Select(path => CapturedInput.Read(path, diagnostics)).OfType<CapturedInput>().ToArray();
        var referenceInputs = references.Select(path => CapturedInput.Read(path, diagnostics)).OfType<CapturedInput>().ToArray();
        if (!LanguageVersionFacts.TryParse(configuration.GetValueOrDefault("LangVersion", "preview"), out var language))
        {
            diagnostics.Add("unsupported-language-version:" + configuration.GetValueOrDefault("LangVersion"));
            language = LanguageVersion.Preview;
        }
        var parse = new CSharpParseOptions(language, preprocessorSymbols: configuration.GetValueOrDefault("DefineConstants", "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        var trees = sourceInputs.Select(input => CSharpSyntaxTree.ParseText(input.SourceText(), parse, input.Path)).ToArray();
        var metadata = referenceInputs.Select(input => MetadataReference.CreateFromImage(ImmutableArray.CreateRange(input.Bytes), filePath: input.Path)).ToArray();
        var compilation = CSharpCompilation.Create(configuration.GetValueOrDefault("AssemblyName", "InventoryPilot"), trees, metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: configuration.GetValueOrDefault("AllowUnsafeBlocks") == "true",
                nullableContextOptions: Enum.TryParse<NullableContextOptions>(configuration.GetValueOrDefault("Nullable"), true, out var nullable) ? nullable : NullableContextOptions.Disable));
        var result = SourceReader.Read(compilation);
        diagnostics.AddRange(sourceInputs.Concat(referenceInputs).Select(input => input.VerifyUnchanged()).OfType<string>());
        configuration["InventoryCompilerVersion"] = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString();
        var document = new InventoryDocument(Inventory.Schema, args[3].Replace('\\', '/'), configuration,
            sourceInputs.Select(input => new InputEntry(Relative(root, input.Path), input.Sha256)).ToArray(), referenceInputs.Select(input => new InputEntry(Relative(root, input.Path), input.Sha256)).ToArray(),
            result.Methods, diagnostics.Concat(result.Diagnostics).Distinct().Order(StringComparer.Ordinal).ToArray());
        File.WriteAllText(args[4], Inventory.Serialize(document) + "\n");
        return 0;
    }
    var source = JsonSerializer.Deserialize<InventoryDocument>(File.ReadAllText(args[1]))!;
    if (args[0] == "reflect")
    {
        var context = new ReflectionReader.ExistingOutputContext(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(args[2]));
        var result = ReflectionReader.Read(assembly);
        var diagnostics = result.Diagnostics.ToList();
        if (assembly.GetName().Name != source.Configuration.GetValueOrDefault("AssemblyName")) diagnostics.Add("reflection-assembly-identity-mismatch");
        File.WriteAllText(args[4], Inventory.Serialize(source with { Methods = result.Methods, Diagnostics = diagnostics.Order(StringComparer.Ordinal).ToArray() }) + "\n");
        File.WriteAllText(args[4] + ".binary.json", Inventory.Serialize(Input(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, Path.GetFullPath(args[2]))) + "\n");
        context.Unload();
        return 0;
    }
    var reflection = JsonSerializer.Deserialize<InventoryDocument>(File.ReadAllText(args[2]))!;
    var comparison = Compare(source, reflection);
    File.WriteAllText(args[4], Inventory.Serialize(comparison) + "\n");
    return 3; // Metadata agreement does not prove binary-to-source binding.
}

static InputEntry Input(string root, string path) => new(Relative(root, path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
static string Relative(string root, string path) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
    ? Path.GetRelativePath(root, path).Replace('\\', '/') : "$external/" + Path.GetFileName(path);

static Comparison Compare(InventoryDocument source, InventoryDocument reflection)
    => PilotFixtures.Compare(source, reflection);

internal sealed record Comparison(string Schema, string Status, bool Qualified, bool ValidatesPullRequest, bool MetadataMatch, bool RuntimeTraitSemanticsVerified, bool BinarySourceBindingVerified, int SourceMethods, int ReflectionMethods, string[] Diagnostics);
