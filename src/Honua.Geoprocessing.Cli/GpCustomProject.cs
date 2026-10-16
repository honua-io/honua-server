// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Honua.Geoprocessing.Cli.Release;
using YamlDotNet.RepresentationModel;

namespace Honua.Geoprocessing.Cli;

/// <summary>The language a scaffolded custom-code project is written in.</summary>
public enum GpProjectLanguage
{
    /// <summary>A Python <c>execute(context)</c> tool run by the Python harness.</summary>
    Python,

    /// <summary>A .NET <c>IGeoprocessingTool</c> run by the .NET harness.</summary>
    Dotnet,
}

/// <summary>
/// The <c>honua-gp.yaml</c> project descriptor <c>honua-gp-devkit init</c> writes next to
/// a copied template. New projects share the release v1 descriptor, with local fixture
/// settings in its authoring section. Existing local-only v0 descriptors remain readable.
/// </summary>
public sealed record GpProjectManifest
{
    /// <summary>The file name.</summary>
    public const string FileName = "honua-gp.yaml";

    /// <summary>The descriptor schema identifier.</summary>
    public const string Schema = "honua.gp-process/v1";

    /// <summary>The original local-only descriptor, still readable for existing projects.</summary>
    public const string LegacySchema = "honua.gp.project/v0";

    /// <summary>The project name (lowercase letters, digits, hyphens).</summary>
    public required string Name { get; init; }

    /// <summary>The language.</summary>
    public required GpProjectLanguage Language { get; init; }

    /// <summary>The template the project was scaffolded from (<c>gp-python</c> or <c>gp-dotnet</c>).</summary>
    public string? Template { get; init; }

    /// <summary>The honua-samples commit the template was taken from.</summary>
    public string? TemplateCommit { get; init; }

    /// <summary>The honua-server checkout holding the harnesses, when <c>init</c> knew it.</summary>
    public string? HarnessRoot { get; init; }

    /// <summary>Exact installed devkit payload version used by this project.</summary>
    public string? ToolchainVersion { get; init; }

    /// <summary>The harness entrypoint (<c>module:function</c> or <c>Assembly::Namespace.Type</c>).</summary>
    public required string Entrypoint { get; init; }

    /// <summary>The source directory (Python package directory or the .csproj's directory).</summary>
    public required string Source { get; init; }

    /// <summary>The .NET project <c>test</c> builds first (builds the process and the harness).</summary>
    public string? Build { get; init; }

    /// <summary>The built process assembly directory (dotnet).</summary>
    public string? AssemblyDir { get; init; }

    /// <summary>The built local harness (dotnet).</summary>
    public string? HarnessDll { get; init; }

    /// <summary>The fixture parameters file.</summary>
    public required string FixtureParams { get; init; }

    /// <summary>Fixture input name to file path.</summary>
    public required IReadOnlyDictionary<string, string> FixtureInputs { get; init; }

    /// <summary>The directory holding the expected artifacts and <c>golden.json</c>.</summary>
    public required string Expected { get; init; }

    /// <summary>Loads and validates a descriptor.</summary>
    /// <param name="path">The <c>honua-gp.yaml</c> path.</param>
    public static GpProjectManifest Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var document = new YamlStream();
        try
        {
            document.Load(new StringReader(yaml));
            if (document.Documents.Count == 1 && document.Documents[0].RootNode is YamlMappingNode root &&
                root.Children.TryGetValue(new YamlScalarNode("schema"), out var schemaNode) &&
                schemaNode is YamlScalarNode { Value: Schema })
            {
                return FromDescriptor(GpProcessDescriptor.Parse(yaml));
            }
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or GpReleaseException)
        {
            throw new InvalidDataException($"{FileName}: {ex.Message}", ex);
        }

        var map = GpFlatYaml.Parse(yaml);
        string Require(string key)
            => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidDataException($"{FileName}: '{key}' is required.");

        var schema = Require("schema");
        if (!string.Equals(schema, LegacySchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{FileName}: schema '{schema}' is not supported (expected {Schema}).");
        }

        var language = Require("language").ToLowerInvariant() switch
        {
            "python" => GpProjectLanguage.Python,
            "dotnet" or "csharp" => GpProjectLanguage.Dotnet,
            var other => throw new InvalidDataException($"{FileName}: language '{other}' is not supported (python or dotnet)."),
        };
        var inputs = map.Where(static kv => kv.Key.StartsWith("fixtures.inputs.", StringComparison.Ordinal))
            .ToDictionary(static kv => kv.Key["fixtures.inputs.".Length..], static kv => kv.Value, StringComparer.Ordinal);
        if (inputs.Count == 0)
        {
            throw new InvalidDataException($"{FileName}: at least one 'fixtures.inputs.<name>' is required.");
        }

        var manifest = new GpProjectManifest
        {
            Name = Require("name"),
            Language = language,
            Template = map.GetValueOrDefault("template"),
            TemplateCommit = map.GetValueOrDefault("templateCommit"),
            HarnessRoot = map.GetValueOrDefault("harnessRoot"),
            Entrypoint = Require("entrypoint"),
            Source = Require("source"),
            Build = map.GetValueOrDefault("build"),
            AssemblyDir = map.GetValueOrDefault("assemblyDir"),
            HarnessDll = map.GetValueOrDefault("harnessDll"),
            FixtureParams = Require("fixtures.params"),
            FixtureInputs = inputs,
            Expected = Require("expected"),
        };
        if (language == GpProjectLanguage.Dotnet && (manifest.Build is null || manifest.AssemblyDir is null || manifest.HarnessDll is null))
        {
            throw new InvalidDataException($"{FileName}: a dotnet project needs 'build', 'assemblyDir' and 'harnessDll'.");
        }

        return manifest;
    }

    private static GpProjectManifest FromDescriptor(GpProcessDescriptor descriptor)
    {
        static string Require(string? value, string key) => !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"{FileName}: '{key}' is required for local testing.");
        var process = descriptor.Process;
        var local = descriptor.Authoring ?? throw new InvalidDataException($"{FileName}: 'authoring' is required for local testing.");
        var language = process?.Runtime switch
        {
            "python" => GpProjectLanguage.Python,
            "dotnet" => GpProjectLanguage.Dotnet,
            _ => throw new InvalidDataException($"{FileName}: process.runtime must be python or dotnet."),
        };
        if (local.Fixtures?.Inputs is not { Count: > 0 })
        {
            throw new InvalidDataException($"{FileName}: at least one authoring.fixtures.inputs entry is required.");
        }

        return new GpProjectManifest
        {
            Name = Require(process?.Name, "process.name"),
            Language = language,
            Entrypoint = Require(process?.Entrypoint, "process.entrypoint"),
            Template = local.Template,
            TemplateCommit = local.TemplateCommit,
            HarnessRoot = local.HarnessRoot,
            ToolchainVersion = local.ToolchainVersion,
            Source = Require(local.Source, "authoring.source"),
            Build = language == GpProjectLanguage.Dotnet ? Require(local.Build, "authoring.build") : local.Build,
            AssemblyDir = language == GpProjectLanguage.Dotnet ? Require(local.AssemblyDir, "authoring.assemblyDir") : local.AssemblyDir,
            HarnessDll = language == GpProjectLanguage.Dotnet ? Require(local.HarnessDll, "authoring.harnessDll") : local.HarnessDll,
            FixtureParams = Require(local.Fixtures.Params, "authoring.fixtures.params"),
            FixtureInputs = local.Fixtures.Inputs,
            Expected = Require(local.Expected, "authoring.expected"),
        };
    }
}

/// <summary>
/// A reader for the flat YAML subset the devkit writes: <c>key: value</c> scalars, nested
/// maps by two-space indentation (flattened to dotted keys), comments and blank lines.
/// Quoted scalars lose their quotes. Anything else is rejected rather than guessed.
/// </summary>
public static partial class GpFlatYaml
{
    /// <summary>Parses the subset into dotted keys.</summary>
    /// <param name="text">The document.</param>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<(int Indent, string Key)>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent % 2 != 0 || line[indent] == '\t')
            {
                throw new InvalidDataException($"line {number}: indentation must be two spaces per level.");
            }

            var match = EntryPattern().Match(trimmed);
            if (!match.Success)
            {
                throw new InvalidDataException($"line {number}: expected 'key: value' (lists and flow syntax are not supported).");
            }

            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var key = string.Join('.', stack.Select(static s => s.Key).Append(match.Groups["key"].Value));
            var value = match.Groups["value"].Value.Trim();
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0 && !value.StartsWith('"') && !value.StartsWith('\''))
            {
                value = value[..comment].TrimEnd();
            }

            if (value.Length == 0)
            {
                stack.Add((indent, match.Groups["key"].Value));
                continue;
            }

            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }

    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_.\-]+):(?:\s+(?<value>.*))?$")]
    private static partial Regex EntryPattern();
}

/// <summary>A file the scaffold writes.</summary>
/// <param name="RelativePath">Path under the project root, forward slashes.</param>
/// <param name="Contents">File bytes.</param>
public sealed record GpProjectFile(string RelativePath, byte[] Contents);

/// <summary>
/// Scaffolds a custom-code project from the honua-samples templates (<c>templates/gp-python</c>,
/// <c>templates/gp-dotnet</c>), which are embedded in the devkit byte for byte together with
/// their <c>TEMPLATE-MANIFEST.json</c> so drift is checkable in both directions. The template
/// already carries a working process, the input schema, fixtures, golden expectations, the
/// dependency lock, golden tests, VS Code launch and task configurations and a container
/// debug recipe; <c>init</c> adds only <c>honua-gp.yaml</c> (and, for .NET, a
/// <c>Directory.Build.rsp</c> that points the template's build at the harness checkout).
/// </summary>
public static partial class GpCustomProjectScaffolder
{
    /// <summary>The honua-samples commit the embedded templates were copied from.</summary>
    public const string TemplateCommit = "04506bba05ed051139b9f248089e57df32f31880";

    /// <summary>The honua-samples repository.</summary>
    public const string TemplateSource = "honua-io/honua-samples";

    private const string ResourcePrefix = "Templates/";

    /// <summary>Validates a project name: lowercase letters, digits, single internal hyphens.</summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="error">Why it was rejected.</param>
    public static bool TryValidateName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length is < 2 or > 63 || !NamePattern().IsMatch(name))
        {
            error = "a project name has 2–63 lowercase letters and digits, starts with a letter, and uses single internal hyphens (e.g. 'parcel-summary').";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>The template name for a language.</summary>
    public static string TemplateFor(GpProjectLanguage language) => language == GpProjectLanguage.Python ? "gp-python" : "gp-dotnet";

    /// <summary>
    /// Finds the honua-server checkout holding the harnesses: <paramref name="explicitRoot"/>,
    /// then <c>HONUA_GP_HARNESS_ROOT</c> / <c>HONUA_SERVER_CHECKOUT</c>, then the bundled
    /// project-local toolchain under <paramref name="startDirectory"/>. No implicit checkout discovery.
    /// </summary>
    public static string? ResolveHarnessRoot(string? explicitRoot, string startDirectory)
    {
        foreach (var candidate in new[] { explicitRoot, Environment.GetEnvironmentVariable("HONUA_GP_HARNESS_ROOT"), Environment.GetEnvironmentVariable("HONUA_SERVER_CHECKOUT") })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsHarnessRoot(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var bundled = Path.Join(Path.GetFullPath(startDirectory), ".honua", "toolchain");
        if (IsHarnessRoot(bundled))
        {
            return bundled;
        }

        return null;
    }

    private static bool IsHarnessRoot(string candidate)
        => Directory.Exists(Path.Join(candidate, "docker", "worker-customcode-python", "harness")) &&
           Directory.Exists(Path.Join(candidate, "docker", "worker-customcode-dotnet", "harness"));

    /// <summary>Lists the embedded files of a template (manifest included), forward-slash paths.</summary>
    /// <param name="template">The template name.</param>
    public static IReadOnlyList<string> EmbeddedFiles(string template)
    {
        var prefix = ResourcePrefix + template + "/";
        return typeof(GpCustomProjectScaffolder).Assembly.GetManifestResourceNames()
            .Select(static n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads one embedded template file.</summary>
    /// <param name="template">The template name.</param>
    /// <param name="relativePath">The file path inside the template, forward slashes.</param>
    public static byte[] ReadEmbedded(string template, string relativePath)
    {
        var assembly = typeof(GpCustomProjectScaffolder).Assembly;
        var wanted = ResourcePrefix + template + "/" + relativePath;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), wanted, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"Template file {wanted} is not embedded in the devkit.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Verifies the embedded template against its manifest: every manifest entry is present
    /// with the recorded SHA-256, and nothing beyond the manifest and the manifest file is
    /// embedded. Returns the problems found (empty when the template is intact).
    /// </summary>
    /// <param name="template">The template name.</param>
    public static IReadOnlyList<string> VerifyEmbedded(string template)
    {
        var problems = new List<string>();
        var files = EmbeddedFiles(template);
        if (!files.Contains("TEMPLATE-MANIFEST.json", StringComparer.Ordinal))
        {
            return [$"{template}: TEMPLATE-MANIFEST.json is not embedded"];
        }

        using var manifest = JsonDocument.Parse(ReadEmbedded(template, "TEMPLATE-MANIFEST.json"));
        var listed = new HashSet<string>(StringComparer.Ordinal) { "TEMPLATE-MANIFEST.json" };
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            listed.Add(path);
            if (!files.Contains(path, StringComparer.Ordinal))
            {
                problems.Add($"{template}/{path}: listed in the manifest but not embedded");
                continue;
            }

            var actual = Convert.ToHexString(SHA256.HashData(ReadEmbedded(template, path))).ToLowerInvariant();
            if (!string.Equals(actual, entry.GetProperty("sha256").GetString(), StringComparison.Ordinal))
            {
                problems.Add($"{template}/{path}: sha256 {actual[..12]}… differs from the manifest");
            }
        }

        problems.AddRange(files.Where(f => !listed.Contains(f)).Select(f => $"{template}/{f}: embedded but not in the manifest"));
        return problems;
    }

    /// <summary>Plans the files for a project: the template, verbatim, plus the devkit's descriptor.</summary>
    /// <param name="language">Python or .NET.</param>
    /// <param name="name">The validated project name.</param>
    /// <param name="harnessRoot">The honua-server checkout holding the harnesses, or null when unknown.</param>
    public static IReadOnlyList<GpProjectFile> Plan(GpProjectLanguage language, string name, string? harnessRoot)
    {
        if (!TryValidateName(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }

        var template = TemplateFor(language);
        var files = EmbeddedFiles(template).Select(path => new GpProjectFile(path, ReadEmbedded(template, path))).ToList();
        if (harnessRoot is null)
        {
            files.AddRange(GpBundledToolchain.Files());
        }

        files.Add(new GpProjectFile(GpProjectManifest.FileName, Encoding.UTF8.GetBytes(Descriptor(language, name, template, harnessRoot))));
        if (language == GpProjectLanguage.Dotnet && harnessRoot is not null)
        {
            // The template's Directory.Build.props expects honua-server as a sibling of a
            // honua-samples checkout unless HonuaServerCheckout is given; the response file
            // supplies it for every dotnet invocation inside the project.
            files.Add(new GpProjectFile("Directory.Build.rsp", Encoding.UTF8.GetBytes($"-p:HonuaServerCheckout={harnessRoot.Replace('\\', '/')}\n")));
        }

        return files;
    }

    /// <summary>Writes a plan to disk; refuses to overwrite existing files.</summary>
    /// <param name="projectDirectory">The project root.</param>
    /// <param name="files">The planned files.</param>
    public static void Apply(string projectDirectory, IReadOnlyList<GpProjectFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(projectDirectory);
        foreach (var file in files)
        {
            if (File.Exists(Path.Join(root, file.RelativePath)))
            {
                throw new IOException($"{file.RelativePath} already exists in {root}; init never overwrites.");
            }
        }

        foreach (var file in files)
        {
            var path = Path.Join(root, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Contents);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    private static string Descriptor(GpProjectLanguage language, string name, string template, string? harnessRoot)
    {
        var runtime = language == GpProjectLanguage.Python ? "python" : "dotnet";
        var entrypoint = language == GpProjectLanguage.Python ? "summary_tool:execute" : "SummaryTool::Honua.Templates.SummaryTool";
        var dependencyLock = language == GpProjectLanguage.Python ? "requirements.lock" : "process/packages.lock.json";
        var buildFiles = language == GpProjectLanguage.Dotnet ? "\n    - Directory.Build.props" : string.Empty;
        var builder = new StringBuilder($"""
            # Shared authoring and immutable release descriptor.
            # Set runtimeImage to your qualified image digest before release.
            schema: {GpProjectManifest.Schema}
            process:
              name: {name}
              version: 0.1.0
              runtime: {runtime}
              entrypoint: {entrypoint}
              contract:
                inputSchema: schema/inputs.schema.json
                outputSchema: schema/outputs.schema.json
            dependencyLock: {dependencyLock}
            workflowVersion: custom-code-v1
            targets:
              - kind: aws-batch
            compatibility:
              serverContractVersion:
                minimum: 1
                maximum: 1
            artifacts:
              include:
                - process
                - schema
                - honua-gp.yaml
                - {dependencyLock}{buildFiles}
            validation:
              receipts:
                - name: worker-image-tests
                  path: artifacts/worker-image-tests.json
            authoring:
              template: {template}
              templateCommit: {TemplateCommit}
              toolchainVersion: {GpBundledToolchain.Version}
            """);
        builder.Append('\n');
        if (harnessRoot is not null)
        {
            builder.Append("  harnessRoot: ").Append(JsonSerializer.Serialize(harnessRoot.Replace('\\', '/'))).Append('\n');
        }

        builder.Append("  source: process\n");
        if (language == GpProjectLanguage.Dotnet)
        {
            builder.Append("  build: tests/SummaryTool.Tests/SummaryTool.Tests.csproj\n  assemblyDir: process/bin/Debug/net10.0\n  harnessDll: tests/SummaryTool.Tests/bin/Debug/net10.0/Honua.CustomCode.Harness.dll\n");
        }

        builder.Append("  fixtures:\n    params: fixtures/params.json\n    inputs:\n      features: fixtures/input.geojson\n  expected: expected\n");
        return builder.ToString();
    }
}

/// <summary>The outcome of <c>honua-gp-devkit test</c> for a custom-code project.</summary>
public sealed record GpCustomProjectTestResult
{
    /// <summary>Whether every expected artifact matched.</summary>
    public required bool Passed { get; init; }

    /// <summary>The receipt path of the run, when the harness produced one.</summary>
    public string? ReceiptPath { get; init; }

    /// <summary>The harness run status.</summary>
    public string? Status { get; init; }

    /// <summary>Per-artifact lines (PASS/FAIL/UPDATED/NOTE with the first difference).</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>A reason when the run itself failed before comparison.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Runs a project's fixtures through the matching local harness and compares the published
/// artifacts with <c>expected/</c>: JSON and GeoJSON structurally with a numeric tolerance,
/// everything else by SHA-256, and the artifact named in <c>expected/golden.json</c> also
/// byte-for-byte against its recorded digest. Failures name the first differing value and the
/// receipt path. <c>--update</c> rewrites the expectations and the golden digest.
/// </summary>
public static class GpCustomProjectTester
{
    /// <summary>Runs the project's test.</summary>
    /// <param name="projectDirectory">The project root holding <c>honua-gp.yaml</c>.</param>
    /// <param name="harnessRoot">The honua-server checkout holding the harnesses.</param>
    /// <param name="update">Whether to rewrite expectations instead of comparing.</param>
    /// <param name="tolerance">Numeric tolerance for JSON comparison.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<GpCustomProjectTestResult> RunAsync(string projectDirectory, string harnessRoot, bool update, double tolerance = 1e-9, CancellationToken cancellationToken = default)
    {
        // MSBuild identifies project instances and incremental outputs by their paths.
        // A shell build resolves its working directory (for example /var -> /private/var
        // on macOS), so use the same physical paths when launching a later devkit build.
        var root = ResolvePhysicalDirectory(projectDirectory);
        harnessRoot = ResolvePhysicalDirectory(harnessRoot);
        var manifest = GpProjectManifest.Load(Path.Join(root, GpProjectManifest.FileName));
        var runsDirectory = Path.Join(root, ".honua", "test-runs");
        Directory.CreateDirectory(runsDirectory);

        var command = manifest.Language == GpProjectLanguage.Python
            ? PythonCommand(manifest, root, harnessRoot, runsDirectory)
            : await DotnetCommandAsync(manifest, root, harnessRoot, runsDirectory, cancellationToken).ConfigureAwait(false);
        if (command.FileName is null)
        {
            return new GpCustomProjectTestResult { Passed = false, Lines = [], Error = command.Arguments[0] };
        }

        var (exit, stdout, stderr) = await RunProcessAsync(command.FileName, command.Arguments, root, command.Environment, cancellationToken).ConfigureAwait(false);
        var lastLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(static l => l.TrimStart().StartsWith('{'));
        string? receiptPath = null;
        string? status = null;
        if (lastLine is not null)
        {
            try
            {
                using var summary = JsonDocument.Parse(lastLine);
                receiptPath = summary.RootElement.TryGetProperty("receipt", out var r) ? r.GetString() : null;
                status = summary.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            }
            catch (JsonException)
            {
                // The harness printed something else last; the exit-code check below reports it.
            }
        }

        if (exit != 0 || receiptPath is null)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return new GpCustomProjectTestResult
            {
                Passed = false,
                ReceiptPath = receiptPath,
                Status = status,
                Lines = [],
                Error = $"harness exited {exit}{(status is null ? "" : $" ({status})")}{(receiptPath is null ? "" : $"; receipt: {receiptPath}")}: {Tail(detail, 600)}",
            };
        }

        var artifactsDirectory = Path.Join(Path.GetDirectoryName(receiptPath)!, "artifacts");
        var expectedDirectory = Path.Join(root, manifest.Expected);
        Directory.CreateDirectory(expectedDirectory);
        var goldenPath = Path.Join(expectedDirectory, "golden.json");
        var lines = new List<string>();

        if (update)
        {
            ReconcileExpectations(artifactsDirectory, expectedDirectory, lines);

            return new GpCustomProjectTestResult { Passed = true, ReceiptPath = receiptPath, Status = status, Lines = lines };
        }

        var passed = true;
        var expectedFiles = Directory.EnumerateFiles(expectedDirectory).Select(Path.GetFileName)
            .Where(static n => !string.Equals(n, "golden.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (expectedFiles.Count == 0)
        {
            return new GpCustomProjectTestResult
            {
                Passed = false,
                ReceiptPath = receiptPath,
                Status = status,
                Lines = [],
                Error = $"{manifest.Expected} holds no expected artifacts; run `test --update` once after reviewing the run's artifacts.",
            };
        }

        foreach (var name in expectedFiles)
        {
            var producedPath = Path.Join(artifactsDirectory, name!);
            if (!File.Exists(producedPath))
            {
                passed = false;
                lines.Add($"FAIL  {name}: the run published no artifact with this name");
                continue;
            }

            var difference = Compare(Path.Join(expectedDirectory, name!), producedPath, tolerance);
            if (difference is null)
            {
                lines.Add($"PASS  {name}");
            }
            else
            {
                passed = false;
                lines.Add($"FAIL  {name}: {difference}");
            }
        }

        if (File.Exists(goldenPath))
        {
            try
            {
                using var golden = JsonDocument.Parse(File.ReadAllText(goldenPath));
                var artifact = golden.RootElement.GetProperty("artifact").GetString()!;
                var wanted = golden.RootElement.GetProperty("sha256").GetString()!;
                var producedPath = Path.Join(artifactsDirectory, artifact);
                if (File.Exists(producedPath))
                {
                    var actual = Hash(producedPath);
                    if (string.Equals(actual, wanted, StringComparison.Ordinal))
                    {
                        lines.Add($"PASS  golden.json: {artifact} sha256 matches");
                    }
                    else
                    {
                        passed = false;
                        lines.Add($"FAIL  golden.json: {artifact} bytes differ (sha256 {actual[..12]}… vs recorded {wanted[..12]}…); `test --update` after an intentional change");
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                passed = false;
                lines.Add($"FAIL  golden.json: not a honua.gp-template-golden.v1 document ({ex.Message})");
            }
        }

        foreach (var extra in Directory.EnumerateFiles(artifactsDirectory).Select(Path.GetFileName).Where(n => !expectedFiles.Contains(n, StringComparer.Ordinal)))
        {
            lines.Add($"NOTE  {extra}: published but not in {manifest.Expected} (add it with --update if intended)");
        }

        return new GpCustomProjectTestResult { Passed = passed, ReceiptPath = receiptPath, Status = status, Lines = lines };
    }

    internal static void ReconcileExpectations(string artifactsDirectory, string expectedDirectory, List<string> lines)
    {
        var producedFiles = Directory.EnumerateFiles(artifactsDirectory).ToArray();
        var producedNames = producedFiles.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Directory.EnumerateFiles(expectedDirectory)
            .Where(path => Path.GetFileName(path) != "golden.json" && !producedNames.Contains(Path.GetFileName(path))))
        {
            File.Delete(stale);
            lines.Add($"REMOVED  {Path.GetFileName(stale)}");
        }

        foreach (var produced in producedFiles)
        {
            File.Copy(produced, Path.Join(expectedDirectory, Path.GetFileName(produced)), overwrite: true);
            lines.Add($"UPDATED  {Path.GetFileName(produced)}");
        }

        var goldenPath = Path.Join(expectedDirectory, "golden.json");
        if (File.Exists(goldenPath) && JsonNode.Parse(File.ReadAllText(goldenPath)) is JsonObject golden &&
            golden["artifact"]?.GetValue<string>() is { } goldenArtifact)
        {
            if (producedNames.Contains(goldenArtifact))
            {
                golden["sha256"] = Hash(Path.Join(artifactsDirectory, goldenArtifact));
                File.WriteAllText(goldenPath, golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                lines.Add($"UPDATED  golden.json (sha256 of {goldenArtifact})");
            }
            else
            {
                File.Delete(goldenPath);
                lines.Add($"REMOVED  golden.json ({goldenArtifact} is no longer produced)");
            }
        }
    }

    /// <summary>
    /// Compares an expected file with a produced one. JSON/GeoJSON are compared structurally
    /// (numbers within <paramref name="tolerance"/>, object key order ignored); other files
    /// byte-for-byte. Returns the first difference, or null when equal.
    /// </summary>
    public static string? Compare(string expectedPath, string producedPath, double tolerance)
    {
        var extension = Path.GetExtension(expectedPath).ToLowerInvariant();
        if (extension is ".json" or ".geojson")
        {
            try
            {
                using var expected = JsonDocument.Parse(File.ReadAllText(expectedPath));
                using var produced = JsonDocument.Parse(File.ReadAllText(producedPath));
                return Diff(expected.RootElement, produced.RootElement, "$", tolerance);
            }
            catch (JsonException ex)
            {
                return $"not valid JSON: {ex.Message}";
            }
        }

        var expectedHash = Hash(expectedPath);
        var producedHash = Hash(producedPath);
        return string.Equals(expectedHash, producedHash, StringComparison.Ordinal)
            ? null
            : $"bytes differ (expected sha256 {expectedHash[..12]}…, produced {producedHash[..12]}…, {new FileInfo(producedPath).Length} bytes)";
    }

    private static string? Diff(JsonElement expected, JsonElement produced, string path, double tolerance)
    {
        if (expected.ValueKind == JsonValueKind.Number && produced.ValueKind == JsonValueKind.Number)
        {
            var e = expected.GetDouble();
            var p = produced.GetDouble();
            return Math.Abs(e - p) <= tolerance * Math.Max(1, Math.Abs(e)) ? null
                : $"{path}: expected {expected.GetRawText()}, produced {produced.GetRawText()}";
        }

        if (expected.ValueKind != produced.ValueKind)
        {
            return $"{path}: expected {expected.ValueKind.ToString().ToLowerInvariant()} {Short(expected)}, produced {produced.ValueKind.ToString().ToLowerInvariant()} {Short(produced)}";
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (!produced.TryGetProperty(property.Name, out var other))
                    {
                        return $"{path}.{property.Name}: missing in produced output";
                    }

                    var inner = Diff(property.Value, other, $"{path}.{property.Name}", tolerance);
                    if (inner is not null)
                    {
                        return inner;
                    }
                }

                foreach (var property in produced.EnumerateObject())
                {
                    if (!expected.TryGetProperty(property.Name, out _))
                    {
                        return $"{path}.{property.Name}: unexpected property {Short(property.Value)}";
                    }
                }

                return null;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToArray();
                var producedItems = produced.EnumerateArray().ToArray();
                if (expectedItems.Length != producedItems.Length)
                {
                    return $"{path}: expected {expectedItems.Length} item(s), produced {producedItems.Length}";
                }

                for (var i = 0; i < expectedItems.Length; i++)
                {
                    var inner = Diff(expectedItems[i], producedItems[i], $"{path}[{i}]", tolerance);
                    if (inner is not null)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonValueKind.String:
                return string.Equals(expected.GetString(), produced.GetString(), StringComparison.Ordinal) ? null
                    : $"{path}: expected {Short(expected)}, produced {Short(produced)}";
            default:
                return null;
        }
    }

    private static string Short(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length > 60 ? text[..60] + "…" : text;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record Command(string? FileName, string[] Arguments, IReadOnlyDictionary<string, string> Environment);

    private static Command PythonCommand(GpProjectManifest manifest, string root, string harnessRoot, string runsDirectory)
    {
        var python = Which("python3") ?? Which("python");
        if (python is null)
        {
            return new Command(null, ["python3 is not on PATH; install Python 3.11+ to run a Python project locally."], new Dictionary<string, string>());
        }

        var arguments = new List<string> { "-m", "honua_customcode_harness.local", "--source", Path.Join(root, manifest.Source), "--entrypoint", manifest.Entrypoint, "--params", Path.Join(root, manifest.FixtureParams), "--output", runsDirectory };
        foreach (var (name, file) in manifest.FixtureInputs)
        {
            arguments.Add("--input");
            arguments.Add($"{name}={Path.Join(root, file)}");
        }

        var harnessPackage = Path.Join(harnessRoot, "docker", "worker-customcode-python", "harness");
        var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        return new Command(python, arguments.ToArray(), new Dictionary<string, string>
        {
            ["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? harnessPackage : harnessPackage + Path.PathSeparator + existing,
        });
    }

    private static string ResolvePhysicalDirectory(string directory)
    {
        var remainingLinks = 64;
        return Resolve(directory);

        string Resolve(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var current = Path.GetPathRoot(fullPath)!;
            // Resolve each ancestor: resolving only the leaf misses symlinks such as /var.
            foreach (var segment in fullPath[current.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, segment);
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: false);
                if (target is null)
                {
                    continue;
                }

                if (--remainingLinks < 0)
                {
                    throw new IOException("The authoring directory contains too many symbolic links or a link cycle.");
                }

                // An absolute link target can itself contain aliased ancestors. Restart
                // from its root instead of reintroducing /var after resolving /private/var.
                current = Resolve(target.FullName);
            }

            return current;
        }
    }

    private static async Task<Command> DotnetCommandAsync(GpProjectManifest manifest, string root, string harnessRoot, string runsDirectory, CancellationToken cancellationToken)
    {
        var dotnet = Which("dotnet") ?? "dotnet";
        var buildProject = Path.Join(root, manifest.Build!);
        // No persistent MSBuild nodes or compiler server: they inherit the redirected pipes and
        // would keep the build's stdout open long after it finished.
        var environment = new Dictionary<string, string>
        {
            ["HONUA_SERVER_CHECKOUT"] = harnessRoot,
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };
        var (buildExit, buildOut, buildErr) = await RunProcessAsync(dotnet,
            ["build", buildProject, "-c", "Debug", "--nologo", "-v", "q", "-nodeReuse:false", "-p:UseSharedCompilation=false", $"-p:HonuaServerCheckout={harnessRoot}"],
            root, environment, cancellationToken).ConfigureAwait(false);
        if (buildExit != 0)
        {
            return new Command(null, [$"dotnet build {manifest.Build} failed: {Tail(string.IsNullOrWhiteSpace(buildErr) ? buildOut : buildErr, 800)}"], environment);
        }

        var harnessDll = Path.Join(root, manifest.HarnessDll!);
        // Build the executable as a root project into the declared launch directory.
        // A transitive reference from a test project can copy the harness DLL while
        // producing an incomplete executable dependency manifest (observed on macOS).
        // Its own build must supply the complete runtime graph, including the SDK.
        var harnessProject = Path.Join(harnessRoot, "docker", "worker-customcode-dotnet", "harness", "src", "Honua.CustomCode.Harness", "Honua.CustomCode.Harness.csproj");
        var (harnessExit, harnessOut, harnessErr) = await RunProcessAsync(dotnet,
            ["build", harnessProject, "-c", "Debug", "--nologo", "-v", "q", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-o", Path.GetDirectoryName(harnessDll)!],
            root, environment, cancellationToken).ConfigureAwait(false);
        if (harnessExit != 0)
        {
            return new Command(null, [$"the local harness build failed: {Tail(string.IsNullOrWhiteSpace(harnessErr) ? harnessOut : harnessErr, 800)}"], environment);
        }

        if (!File.Exists(harnessDll))
        {
            return new Command(null, [$"the build did not produce {manifest.HarnessDll}; the test project must reference the local harness."], environment);
        }

        var arguments = new List<string> { harnessDll, "local", "--assembly-dir", Path.Join(root, manifest.AssemblyDir!), "--entrypoint", manifest.Entrypoint, "--params", Path.Join(root, manifest.FixtureParams), "--output", runsDirectory };
        foreach (var (name, file) in manifest.FixtureInputs)
        {
            arguments.Add("--input");
            arguments.Add($"{name}={Path.Join(root, file)}");
        }

        return new Command(dotnet, arguments.ToArray(), environment);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static string? Which(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { executable + ".exe", executable + ".cmd", executable + ".bat" } : [executable];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in names.Select(name => Path.Join(directory, name)))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Tail(string text, int length)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= length ? trimmed : "…" + trimmed[^length..];
    }
}
