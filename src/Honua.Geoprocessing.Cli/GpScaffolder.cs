// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Geoprocessing.Cli;

public enum GpProcessKind
{
    Geometry,

    Gdal,
}

public sealed record GpScaffoldFile(string RelativePath, string Contents, string Description);

public sealed record GpScaffoldPlan(
    string ProcessId,
    GpProcessKind Kind,
    string FixtureId,
    IReadOnlyList<GpScaffoldFile> Files,
    string NextSteps);

public static class GpScaffolder
{
    public const string ManagedExecutorDirectory =
        "src/Honua.Geoprocessing/Features/Geoprocessing/Execution";

    public const string GdalExecutorDirectory =
        "src/Honua.Worker.Gdal/Execution";

    public const string FixtureRoot = "samples/gp";

    public static bool TryValidateProcessId(string? processId, out string error)
    {
        if (string.IsNullOrWhiteSpace(processId))
        {
            error = "process id must not be empty.";
            return false;
        }

        if (processId.Length > 96)
        {
            error = "process id must be 96 characters or fewer.";
            return false;
        }

        var segments = processId.Split('.');
        var invalidSegment = segments.FirstOrDefault(segment => !IsValidSegment(segment));
        if (invalidSegment is not null)
        {
            error =
                $"invalid segment '{invalidSegment}' in process id '{processId}'. Each dot-separated "
                + "segment must start with a lowercase letter and contain only lowercase letters, "
                + "digits, and single internal hyphens (e.g. 'geometry.buffer', 'analytics.spatial-join').";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] is < 'a' or > 'z')
        {
            return false;
        }

        var previousWasHyphen = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '-')
            {
                // No leading/trailing/double hyphen.
                if (i == 0 || i == segment.Length - 1 || previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (c is (< 'a' or > 'z') and (< '0' or > '9'))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }

    public static string ToTypeStem(string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);

        var sb = new StringBuilder(processId.Length);
        var capitalizeNext = true;
        foreach (var c in processId)
        {
            if (c is '.' or '-')
            {
                capitalizeNext = true;
                continue;
            }

            if (capitalizeNext)
            {
                sb.Append(char.ToUpper(c, CultureInfo.InvariantCulture));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string ToFixtureId(string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        return processId.Replace('.', '-');
    }

    public static GpScaffoldPlan Plan(
        string processId,
        GpProcessKind kind,
        IReadOnlyCollection<string> existingProcessIds)
    {
        ArgumentNullException.ThrowIfNull(existingProcessIds);

        if (!TryValidateProcessId(processId, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(processId));
        }

        if (existingProcessIds.Contains(processId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Process id '{processId}' is already registered. Pick a new id "
                + "(run 'honua gp list' to see the registered processes).");
        }

        var typeStem = ToTypeStem(processId);
        var fixtureId = ToFixtureId(processId);
        var className = typeStem + (kind == GpProcessKind.Gdal ? "NativeJobExecutor" : "JobExecutor");

        var executorDirectory = kind == GpProcessKind.Gdal ? GdalExecutorDirectory : ManagedExecutorDirectory;
        var registrationFile = kind == GpProcessKind.Gdal
            ? "src/Honua.Worker.Gdal/GdalWorkerServiceCollectionExtensions.cs"
            : "src/Honua.Geoprocessing/Features/Geoprocessing/GeoprocessingServiceCollectionExtensions.cs";
        var registrationCall = kind == GpProcessKind.Gdal
            ? $"RegisterGdalExecutor<{className}>(services);"
            : $"Register<{className}>(services);";

        var files = new List<GpScaffoldFile>
        {
            new(
                $"{executorDirectory}/{className}.cs",
                RenderExecutor(processId, className, kind),
                "Executor — declares the ProcessId + typed param schema; edit the TODO body."),
            new(
                $"{FixtureRoot}/{fixtureId}/fixture.json",
                RenderFixtureManifest(processId, fixtureId, kind),
                "Golden-test fixture manifest (P6) — `gp test` runs this."),
            new(
                $"{FixtureRoot}/{fixtureId}/{GoldenFileName(kind)}",
                RenderGolden(processId, kind),
                "Golden file — the expected trivial result; regenerate with `gp test --update`."),
            new(
                $"{FixtureRoot}/{fixtureId}/README.md",
                RenderNote(processId, kind, className, executorDirectory, registrationFile, registrationCall),
                "Generated note — edit → run → test → plan."),
        };

        var nextSteps = RenderNextSteps(processId, kind, className, executorDirectory, registrationFile, registrationCall, fixtureId);
        return new GpScaffoldPlan(processId, kind, fixtureId, files, nextSteps);
    }

    private static string GoldenFileName(GpProcessKind kind) =>
        kind == GpProcessKind.Gdal ? "golden.json" : "golden.geojson";

    internal static string RenderExecutor(string processId, string className, GpProcessKind kind)
    {
        var ns = kind == GpProcessKind.Gdal ? "Honua.Worker.Gdal.Execution" : "Honua.Geoprocessing.Execution";
        var nativeProfile = kind == GpProcessKind.Gdal;

        // Geometry kind emits a vector GeoJSON Feature (geometry comparator);
        // GDAL kind emits a scalar JSON document (scalar/structural comparator). Both build
        // the data URI inline with System.Text.Json so the template has zero dependence on
        // internal helpers and compiles as soon as it is written.
        var mediaType = nativeProfile ? "application/json" : "application/geo+json";
        var trivialPayload = "using var buffer = new MemoryStream();\n"
            + "        using (var writer = new Utf8JsonWriter(buffer))\n"
            + "        {\n"
            + "            writer.WriteStartObject();\n";
        if (!nativeProfile)
        {
            trivialPayload += "            writer.WriteString(\"type\", \"Feature\");\n"
                + "            writer.WriteStartObject(\"geometry\");\n"
                + "            writer.WriteString(\"type\", \"Point\");\n"
                + "            writer.WriteStartArray(\"coordinates\");\n"
                + "            writer.WriteNumberValue(0.0);\n"
                + "            writer.WriteNumberValue(0.0);\n"
                + "            writer.WriteEndArray();\n"
                + "            writer.WriteEndObject();\n"
                + "            writer.WriteStartObject(\"properties\");\n";
        }
        trivialPayload += "            writer.WriteString(\"processId\", HandledProcessId);\n"
            + "            writer.WriteString(\"status\", \"scaffolded\");\n"
            + "            writer.WriteString(\"echo\", echo);\n";
        if (!nativeProfile)
        {
            trivialPayload += "            writer.WriteEndObject();\n";
        }
        trivialPayload += "            writer.WriteEndObject();\n"
            + "        }\n"
            + "        var payload = buffer.ToArray();\n";

        var profileMember = nativeProfile
            ? "\n    private static readonly IReadOnlySet<string> NativeProfileSet =\n"
                + "        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };\n"
                + "\n    /// <inheritdoc />\n"
                + "    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;\n"
            : string.Empty;

        return
$@"// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace {ns};

/// <summary>
/// SCAFFOLDED executor for the <c>{processId}</c> process (generated by <c>honua gp new</c>,
/// GP Devkit P4). It declares the process id it handles and reads its typed step-0 inputs,
/// but its body is a TODO that returns a trivial, deterministic valid result so the process
/// compiles, auto-registers, runs (<c>gp run {processId}</c>), and golden-tests
/// (<c>gp test {ToFixtureId(processId)}</c>) immediately. Replace the marked body with the
/// real implementation, then regenerate the golden with <c>gp test {ToFixtureId(processId)} --update</c>.
/// </summary>
internal sealed class {className} : IProcessExecutor
{{
    /// <summary>The single canonical process id this executor handles.</summary>
    public const string HandledProcessId = ""{processId}"";

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds {{ get; }} =
        new HashSet<string>(StringComparer.Ordinal) {{ HandledProcessId }};

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;
{profileMember}
    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {{
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, ""Parsing {processId} inputs"", cancellationToken).ConfigureAwait(false);

        // Step-0 inputs arrive under the canonical 'geoprocessing.step.0.<name>' keys.
        // The scaffold reads one optional 'value' input and echoes it; add the typed
        // inputs your process needs and validate them here (return JobExecutionResult.Failed
        // with a classified message on bad input).
        var parameters = job.Spec.Parameters;
        var inputPrefix = $""{{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}}0."";
        var echo = parameters.TryGetValue(inputPrefix + ""value"", out var value) && !string.IsNullOrEmpty(value)
            ? value
            : ""scaffold"";

        // ----------------------------------------------------------------------
        // TODO: replace this trivial body with the real {processId} implementation.
        //   1. Read + validate your typed inputs (see GeometryCentroidJobExecutor /
        //      GdalVectorConvertJobExecutor for the input-reading + size-cap patterns).
        //   2. Compute the result.
        //   3. Publish the artifact as a data URI (geometry ops emit application/geo+json;
        //      scalar/raster ops emit application/json or a native media type).
        // ----------------------------------------------------------------------
        await context.ReportProgressAsync(50, ""Computing {processId}"", cancellationToken).ConfigureAwait(false);

        {trivialPayload}

        var artifactUri = ""data:{mediaType};base64,"" + Convert.ToBase64String(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, ""{processId} completed"", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }}
}}
";
    }

    internal static string RenderFixtureManifest(string processId, string fixtureId, GpProcessKind kind)
    {
        var mode = kind == GpProcessKind.Gdal ? "scalarStructural" : "geometry";
        var golden = GoldenFileName(kind);
        return
$@"{{
  ""id"": ""{fixtureId}"",
  ""process"": ""{processId}"",
  ""description"": ""SCAFFOLDED golden fixture for {processId} (honua gp new). Asserts the trivial scaffold result; once you edit the executor body, update the inputs + golden (gp test {fixtureId} --update)."",
  ""mode"": ""{mode}"",
  ""tolerance"": {{ ""coordinate"": 1e-6, ""numeric"": 1e-6 }},
  ""inputs"": {{
    ""value"": ""hello""
  }},
  ""golden"": ""{golden}""
}}
";
    }

    internal static string RenderGolden(string processId, GpProcessKind kind) =>
        kind == GpProcessKind.Gdal
            ? $"{{\"processId\":\"{processId}\",\"status\":\"scaffolded\",\"echo\":\"hello\"}}"
            : "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[0,0]},"
                + $"\"properties\":{{\"processId\":\"{processId}\",\"status\":\"scaffolded\",\"echo\":\"hello\"}}}}";

    private static string RenderNote(
        string processId,
        GpProcessKind kind,
        string className,
        string executorDirectory,
        string registrationFile,
        string registrationCall)
    {
        var fixtureId = ToFixtureId(processId);
        var kindLabel = kind == GpProcessKind.Gdal ? "gdal (native)" : "geometry (managed)";
        return
$@"# {processId} (scaffolded by `honua gp new`)

Kind: {kindLabel}

This process was generated ready-to-edit, registered, runnable, and golden-tested.

## Files
- `{executorDirectory}/{className}.cs` — the executor. Edit the TODO body.
- `{FixtureRoot}/{fixtureId}/fixture.json` — the P6 golden fixture.
- `{FixtureRoot}/{fixtureId}/{GoldenFileName(kind)}` — the expected result.

## Wire-up (one line — done for you if you used `gp new` without `--output`)
Add this to `{registrationFile}`:

    {registrationCall}

## Dev loop
1. Edit the body in `{className}.cs` (read + validate inputs, compute, publish the artifact).
2. Run it:    `honua gp run {processId} --param value=hello`
3. Update the golden to match your new output:  `honua gp test {fixtureId} --update`
4. Test it:   `honua gp test {fixtureId}`
5. Plan it:   `honua gp plan {processId}`   (validates the typed param schema)

No Redis, no job store, no control plane — the whole loop runs offline.
";
    }

    private static string RenderNextSteps(
        string processId,
        GpProcessKind kind,
        string className,
        string executorDirectory,
        string registrationFile,
        string registrationCall,
        string fixtureId)
    {
        var sb = new StringBuilder();
        sb.Append("Next steps:\n");
        sb.Append($"  1. Edit the body in {executorDirectory}/{className}.cs (look for the TODO).\n");
        sb.Append($"  2. Run it:    honua gp run {processId} --param value=hello\n");
        sb.Append($"  3. Test it:   honua gp test {fixtureId}\n");
        sb.Append($"  4. Plan it:   honua gp plan {processId}\n");
        sb.Append($"\nIf you scaffolded with --output, also add this line to {registrationFile}:\n");
        sb.Append($"     {registrationCall}\n");
        sb.Append("  add a matching ProcessDefinition to BuiltInProcessCatalog, and classify the id in ProcessExecutionCapabilityCatalog.JobProcessIds.\n");
        return sb.ToString();
    }
}
