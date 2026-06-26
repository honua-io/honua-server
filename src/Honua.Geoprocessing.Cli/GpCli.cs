// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Cli.Publish;
using Honua.Geoprocessing.LocalRunner;
using Honua.Geoprocessing.Testing;
using Honua.Worker.Gdal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Cli;

/// <summary>
/// The <c>honua gp</c> dev tool (GP Devkit, issue #2123): a headless, Redis-free
/// geoprocessing dev loop. It builds the SAME managed + native GDAL
/// <see cref="IProcessExecutor"/> set the serving/worker hosts register, then drives
/// a single process directly through <see cref="GeoprocessingLocalRunner"/> — with
/// NO Redis, NO job store, NO queue, and NO control plane — so the inner-loop GP
/// edit/run cycle is sub-second and works fully offline. It is a dev tool only and is
/// deliberately kept out of the AOT-published server surface.
/// </summary>
public static class GpCli
{
    /// <summary>
    /// Parses argv and dispatches to the <c>list</c> or <c>run</c> verb.
    /// </summary>
    /// <param name="args">Process arguments (the verb plus its options).</param>
    /// <returns>A process exit code: 0 success, non-zero on usage error or a failed run.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var verb = args[0];
        var rest = args[1..];

        try
        {
            return verb switch
            {
                "list" => RunList(),
                "describe" => RunDescribe(rest),
                "run" => await RunRun(rest).ConfigureAwait(false),
                "test" => await RunTest(rest).ConfigureAwait(false),
                "plan" => await RunPlan(rest).ConfigureAwait(false),
                "new" => await RunNew(rest).ConfigureAwait(false),
                "publish" => await RunPublish(rest).ConfigureAwait(false),
                "-h" or "--help" or "help" => PrintUsageAndOk(),
                _ => Fail($"Unknown command '{verb}'."),
            };
        }
        catch (GpCliUsageException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static ServiceProvider BuildProvider(GlassBoxCapture? glassBox = null)
    {
        // Empty in-memory configuration: AddGeoprocessing binds its option sections
        // from configuration but every option has a valid default, and the
        // Redis-conditional durable-store registrations are skipped because no
        // IConnectionMultiplexer is present. The result is the full managed +
        // native executor set with zero external dependencies.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

        // Managed geometry/analytics/transform/source/sink executors.
        services.AddGeoprocessing(configuration);
        // Native GDAL executors (Redis-free seam: options + CLI runner + executors only).
        services.AddGdalProcessExecutors(configuration);

        // DEV-ONLY: when the caller opted into glass-box mode, decorate the native GDAL
        // command runner so the unsanitized command + full stdout/stderr are captured.
        // This is the ONLY place that decorator is wired; the production worker host
        // (AddGdalWorker) never installs it, so the sanitized path stays the default.
        if (glassBox is not null)
        {
            services.AddGlassBoxGdalCapture(glassBox);
        }

        return services.BuildServiceProvider();
    }

    private static int RunList()
    {
        using var provider = BuildProvider();
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GeoprocessingLocalRunner(executors);
        var catalog = provider.GetService<IProcessCatalog>();

        Console.WriteLine("Available geoprocessing processes:");
        foreach (var id in runner.AvailableProcessIds)
        {
            var def = catalog?.GetProcess(id);
            if (def is not null)
            {
                Console.WriteLine($"  {id,-32} {def.Title}");
            }
            else
            {
                Console.WriteLine($"  {id}");
            }
        }

        return 0;
    }

    private static int RunDescribe(string[] args)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException(
                "Missing <processId>. Usage: honua gp describe <processId> [--json]");
        }

        var processId = args[0];
        var asJson = false;
        for (var i = 1; i < args.Length; i++)
        {
            asJson = args[i] switch
            {
                "--json" or "-j" => true,
                _ => throw new GpCliUsageException($"Unknown option '{args[i]}'."),
            };
        }

        // Resolve from the SAME live catalog `list`/`run`/`plan` use, so a described
        // process is exactly one the runner can invoke — id, typed params, outputs,
        // and runtime profile all come from the registered ProcessDefinition.
        using var provider = BuildProvider();
        var catalog = provider.GetService<IProcessCatalog>()
            ?? throw new GpCliUsageException("Process catalog is unavailable.");

        var definition = catalog.GetProcess(processId);
        if (definition is null)
        {
            // Mirror `run`'s unknown-process usage error rather than a crash.
            throw new GpCliUsageException(
                $"Process id '{processId}' is not registered. Run 'honua gp list' to see available processes.");
        }

        Console.WriteLine(asJson
            ? GpDescribeRenderer.RenderJson(definition)
            : GpDescribeRenderer.RenderText(definition));
        return 0;
    }

    private static async Task<int> RunRun(string[] args)
    {
        var parsed = ParseProcessArgs(
            args,
            "honua gp run <id> --input <file> [--param k=v] [--out <file>]");

        // Glass-box (dev-only) is gated behind an EXPLICIT opt-in: the --glass-box/--debug
        // flag OR the HONUA_GP_GLASSBOX env truthy. Absent both, the run takes the default
        // sanitized path (no raw scratch paths / no untruncated stderr) — the same output a
        // production-equivalent caller sees.
        var glassBoxEnabled = parsed.GlassBoxRequested || IsEnvTruthy("HONUA_GP_GLASSBOX");
        var capture = glassBoxEnabled ? new GlassBoxCapture() : null;

        using var provider = BuildProvider(capture);
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GeoprocessingLocalRunner(executors);

        if (!runner.AvailableProcessIds.Contains(parsed.ProcessId, StringComparer.Ordinal))
        {
            throw new GpCliUsageException(
                $"Process id '{parsed.ProcessId}' is not registered. Run 'honua gp list' to see available processes.");
        }

        var inputs = new Dictionary<string, string>(parsed.Params, StringComparer.Ordinal);
        var catalog = provider.GetService<IProcessCatalog>();
        await BindFileInputAsync(parsed.ProcessId, parsed.InputPath, catalog, inputs).ConfigureAwait(false);

        var result = await runner.RunAsync(parsed.ProcessId, inputs, capture).ConfigureAwait(false);

        Console.WriteLine($"process : {result.ProcessId}");
        Console.WriteLine($"status  : {result.Status}");
        Console.WriteLine($"elapsed : {result.Elapsed.TotalMilliseconds:F1} ms");

        foreach (var log in result.Logs)
        {
            if (log.Metadata is not null
                && log.Metadata.TryGetValue("gdal.command", out var command))
            {
                // Default path prints the SANITIZED command (scratch redacted to <scratch>).
                Console.WriteLine($"command : {command}");
            }
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning : {warning}");
        }

        if (result.GlassBox is not null)
        {
            PrintGlassBox(result.GlassBox);
        }

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"error   : {result.ErrorMessage}");
            return 1;
        }

        foreach (var artifact in result.Artifacts)
        {
            Console.WriteLine($"artifact: {Summarize(artifact)}");
        }

        if (parsed.OutPath is not null)
        {
            if (result.Artifacts.Count == 0)
            {
                Console.Error.WriteLine("error   : run succeeded but produced no artifact to write to --out.");
                return 1;
            }

            var bytes = DecodeArtifact(result.Artifacts[0]);
            await File.WriteAllBytesAsync(parsed.OutPath, bytes).ConfigureAwait(false);
            Console.WriteLine($"wrote   : {parsed.OutPath} ({bytes.Length} bytes)");
        }

        return 0;
    }

    private static bool IsEnvTruthy(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    /// <summary>
    /// Renders the DEV-ONLY glass-box section (issue #2128): the phase/timeline, the
    /// UNSANITIZED native command(s) with their real scratch paths and FULL stdout/stderr,
    /// the scratch directories, an artifact preview, and a "repro locally" hint. This block
    /// only ever prints when the run was driven under the explicit glass-box opt-in.
    /// </summary>
    private static void PrintGlassBox(GlassBoxReport glassBox)
    {
        Console.WriteLine();
        Console.WriteLine("──── glass box (dev) ────────────────────────────────────────");

        Console.WriteLine("timeline:");
        if (glassBox.Timeline.Count == 0)
        {
            Console.WriteLine("  (no phases reported)");
        }
        else
        {
            foreach (var phase in glassBox.Timeline)
            {
                var pct = phase.PercentComplete is { } p ? $"{p,5:F0}%" : "     ";
                Console.WriteLine($"  +{phase.Elapsed.TotalMilliseconds,7:F1} ms  {pct}  {phase.Phase}");
            }
        }

        if (glassBox.Commands.Count == 0)
        {
            Console.WriteLine("native commands: (none — managed op, no GDAL subprocess)");
        }
        else
        {
            Console.WriteLine("native commands (UNSANITIZED — real scratch paths):");
            foreach (var command in glassBox.Commands)
            {
                Console.WriteLine($"  $ {command.CommandLine}");
                Console.WriteLine($"    cwd      : {command.WorkingDirectory}");
                Console.WriteLine($"    exit     : {command.ExitCode}");
                if (!string.IsNullOrWhiteSpace(command.StandardOutput))
                {
                    Console.WriteLine("    stdout   :");
                    PrintIndented(command.StandardOutput, "      ");
                }

                if (!string.IsNullOrWhiteSpace(command.StandardError))
                {
                    Console.WriteLine("    stderr   :");
                    PrintIndented(command.StandardError, "      ");
                }
            }
        }

        if (glassBox.ScratchDirectories.Count > 0)
        {
            Console.WriteLine("scratch dirs (inspect intermediate files here):");
            foreach (var dir in glassBox.ScratchDirectories)
            {
                Console.WriteLine($"  {dir}");
            }

            Console.WriteLine("repro locally:");
            foreach (var command in glassBox.Commands)
            {
                Console.WriteLine($"  cd {command.WorkingDirectory} && {command.CommandLine}");
            }
        }

        if (glassBox.ArtifactPreviews.Count == 0)
        {
            Console.WriteLine("artifact preview: (no artifact published)");
        }
        else
        {
            Console.WriteLine("artifact preview:");
            foreach (var preview in glassBox.ArtifactPreviews)
            {
                Console.WriteLine($"  type={preview.MediaType} size={preview.SizeBytes}B");
                PrintIndented(preview.Summary, "    ");
            }
        }

        Console.WriteLine("─────────────────────────────────────────────────────────────");
    }

    private static void PrintIndented(string text, string indent)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Console.WriteLine(indent + line);
        }
    }

    private static async Task<int> RunPlan(string[] args)
    {
        var parsed = ParseProcessArgs(
            args,
            "honua gp plan <id> --input <file> [--param k=v ...]");

        if (parsed.OutPath is not null)
        {
            throw new GpCliUsageException("'plan' does not run the process, so --out is not supported.");
        }

        using var provider = BuildProvider();
        var catalog = provider.GetService<IProcessCatalog>()
            ?? throw new GpCliUsageException("Process catalog is unavailable.");
        var maxArtifactBytes = provider
            .GetRequiredService<IOptions<GeoprocessingExecutorOptions>>()
            .Value.MaxArtifactBytes;

        // Resolve the same inputs `run` would: merge --param then bind --input. We
        // also capture the raw decoded input size so the estimate has an anchor.
        var inputs = new Dictionary<string, string>(parsed.Params, StringComparer.Ordinal);
        var callerInputBytes = await BindFileInputAsync(parsed.ProcessId, parsed.InputPath, catalog, inputs)
            .ConfigureAwait(false);

        var plan = GpPlanner.Build(parsed.ProcessId, inputs, catalog, maxArtifactBytes, callerInputBytes);
        if (plan is null)
        {
            // Mirror `run`'s "unknown process" usage error rather than a crash.
            throw new GpCliUsageException(
                $"Process id '{parsed.ProcessId}' is not registered. Run 'honua gp list' to see available processes.");
        }

        PrintPlan(plan);

        // Exit non-zero when the plan is invalid so scripts/CI can gate a submit on
        // a clean plan; a valid plan with only size/cost warnings still exits 0.
        return plan.IsValid ? 0 : 1;
    }

    private static void PrintPlan(GpPlan plan)
    {
        Console.WriteLine($"process      : {plan.ProcessId}  ({plan.Title})");
        Console.WriteLine($"category     : {plan.Category}");
        Console.WriteLine($"runtime      : {plan.RuntimeProfile}");
        Console.WriteLine($"valid        : {(plan.IsValid ? "yes" : "NO")}");
        Console.WriteLine();

        Console.WriteLine("steps:");
        foreach (var step in plan.Steps)
        {
            var deps = step.DependsOn.Count == 0 ? "-" : string.Join(", ", step.DependsOn);
            Console.WriteLine($"  {step.StepId}  {step.ProcessId}  (depends on: {deps})");
            foreach (var param in step.Parameters)
            {
                var req = param.Required ? "required" : "optional";
                var value = param.DisplayValue ?? "(unset)";
                var src = param.Source switch
                {
                    GpParamSource.Caller => "caller",
                    GpParamSource.Default => "default",
                    _ => "unset",
                };
                Console.WriteLine($"    - {param.Name} [{param.ValueType}, {req}] = {value}  ({src})");
            }
        }
        Console.WriteLine();

        var outputs = plan.Outputs.Count == 0 ? "-" : string.Join(", ", plan.Outputs);
        Console.WriteLine($"outputs      : {outputs}");
        Console.WriteLine();

        var estimate = plan.Estimate;
        Console.WriteLine("size/cost estimate (HEURISTIC — not a guarantee):");
        Console.WriteLine($"  input bytes      : {GpPlanner.FormatBytes(estimate.InputBytes)}");
        Console.WriteLine(estimate.EstimatedOutputBytes is { } outBytes
            ? $"  est. output      : ~{GpPlanner.FormatBytes(outBytes)}"
            : "  est. output      : (not estimable offline)");
        Console.WriteLine($"  cap (MaxArtifactBytes) : {GpPlanner.FormatBytes(estimate.MaxArtifactBytes)}");
        Console.WriteLine($"  basis            : {estimate.Basis}");
        Console.WriteLine($"  resource hint    : profile={plan.RuntimeProfile}, long-running={(estimate.LongRunning ? "yes" : "no")}");
        Console.WriteLine();

        if (plan.Errors.Count > 0)
        {
            Console.WriteLine("errors (block submit):");
            foreach (var error in plan.Errors)
            {
                Console.WriteLine($"  ✗ {error}");
            }
            Console.WriteLine();
        }

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine("warnings:");
            foreach (var warning in plan.Warnings)
            {
                Console.WriteLine($"  ! {warning}");
            }
            Console.WriteLine();
        }

        Console.WriteLine(plan.IsValid
            ? "Plan is valid. Submit with: honua gp run " + plan.ProcessId + " ..."
            : "Plan is INVALID — fix the errors above before submitting.");
    }

    /// <summary>
    /// Parses the shared <c>&lt;processId&gt; [--input f] [--param k=v ...] [--out f]</c>
    /// argument shape used by both <c>run</c> and <c>plan</c>.
    /// </summary>
    private static ProcessArgs ParseProcessArgs(string[] args, string usage)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException($"Missing <processId>. Usage: {usage}");
        }

        var processId = args[0];
        string? inputPath = null;
        string? outPath = null;
        var glassBoxRequested = false;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" or "-i":
                    inputPath = NextValue(args, ref i, "--input");
                    break;
                case "--out" or "-o":
                    outPath = NextValue(args, ref i, "--out");
                    break;
                case "--glass-box" or "--debug" or "-d":
                    glassBoxRequested = true;
                    break;
                case "--param" or "-p":
                    var kv = NextValue(args, ref i, "--param");
                    var eq = kv.IndexOf('=', StringComparison.Ordinal);
                    if (eq <= 0)
                    {
                        throw new GpCliUsageException($"Invalid --param '{kv}'; expected key=value.");
                    }

                    inputs[kv[..eq]] = kv[(eq + 1)..];
                    break;
                default:
                    throw new GpCliUsageException($"Unknown option '{args[i]}'.");
            }
        }

        return new ProcessArgs(processId, inputPath, outPath, glassBoxRequested, inputs);
    }

    /// <summary>
    /// Binds <c>--input &lt;file&gt;</c>: reads the file, base64-encodes its bytes, and
    /// assigns it to the process's primary file-like input (unless the caller already
    /// supplied that key via <c>--param</c>). Returns the raw (decoded) byte length of
    /// the supplied input so the plan size estimate has an anchor — 0 when no file
    /// input was bound.
    /// </summary>
    private static async Task<long> BindFileInputAsync(
        string processId,
        string? inputPath,
        IProcessCatalog? catalog,
        Dictionary<string, string> inputs)
    {
        if (inputPath is null)
        {
            return 0;
        }

        if (!File.Exists(inputPath))
        {
            throw new GpCliUsageException($"--input file not found: {inputPath}");
        }

        var inputKey = ResolveFileInputKey(catalog?.GetProcess(processId));
        if (inputKey is null)
        {
            throw new GpCliUsageException(
                $"Process '{processId}' has no file-like input to bind --input to; supply inputs with --param instead.");
        }

        var bytes = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
        if (!inputs.ContainsKey(inputKey))
        {
            inputs[inputKey] = Convert.ToBase64String(bytes);
        }

        return bytes.LongLength;
    }

    private static async Task<int> RunTest(string[] args)
    {
        string? root = null;
        string? onlyId = null;
        var update = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" or "-r":
                    root = NextValue(args, ref i, "--root");
                    break;
                case "--update" or "-u":
                    update = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new GpCliUsageException($"Unknown option '{args[i]}'.");
                    }

                    if (onlyId is not null)
                    {
                        throw new GpCliUsageException("Specify at most one fixture id.");
                    }

                    onlyId = args[i];
                    break;
            }
        }

        var fixtureRoot = ResolveFixtureRoot(root);
        if (fixtureRoot is null)
        {
            throw new GpCliUsageException(
                "Could not locate a GP fixtures directory. Pass --root <dir> (e.g. --root samples/gp).");
        }

        IReadOnlyList<GoldenFixture> fixtures;
        try
        {
            fixtures = GoldenFixtureLoader.Discover(fixtureRoot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error   : failed to load fixtures from {fixtureRoot}: {ex.Message}");
            return 1;
        }

        if (onlyId is not null)
        {
            fixtures = fixtures.Where(f => string.Equals(f.Id, onlyId, StringComparison.Ordinal)).ToArray();
            if (fixtures.Count == 0)
            {
                throw new GpCliUsageException($"No GP fixture with id '{onlyId}' under {fixtureRoot}.");
            }
        }

        // Update mode is opt-in here too: --update sets the same env var the SDK reads, so
        // a normal `gp test` can never silently overwrite a golden.
        var updateMode = update || GpGoldenAssert.UpdateModeEnabled
            ? GoldenUpdateMode.Update
            : GoldenUpdateMode.Assert;

        using var provider = BuildProvider();
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GpProcessTestRunner(executors);

        Console.WriteLine($"GP golden tests : {fixtures.Count} fixture(s) under {fixtureRoot}");
        Console.WriteLine($"mode            : {(updateMode == GoldenUpdateMode.Update ? "UPDATE (regenerating goldens)" : "assert")}");
        Console.WriteLine();

        var passed = 0;
        var failed = 0;
        foreach (var fixture in fixtures)
        {
            var result = await runner.RunAsync(fixture, updateMode).ConfigureAwait(false);
            if (result.Passed)
            {
                passed++;
                Console.WriteLine($"PASS  {fixture.Id,-28} {fixture.ProcessId,-18} {result.Reason}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  {fixture.Id,-28} {fixture.ProcessId,-18} {result.Reason}");
                foreach (var line in (result.Comparison?.Format() ?? result.FormatFailure())
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Console.WriteLine($"      {line.TrimEnd()}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"summary : {passed} passed, {failed} failed, {fixtures.Count} total");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> RunNew(string[] args)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException(
                "Missing <processId>. Usage: honua gp new <id> [--kind geometry|gdal] [--output <dir>]");
        }

        var processId = args[0];
        var kind = GpProcessKind.Geometry;
        string? outputDir = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--kind" or "-k":
                    var kindRaw = NextValue(args, ref i, "--kind");
                    kind = kindRaw.ToLowerInvariant() switch
                    {
                        "geometry" or "managed" => GpProcessKind.Geometry,
                        "gdal" or "native" => GpProcessKind.Gdal,
                        _ => throw new GpCliUsageException(
                            $"Unknown --kind '{kindRaw}'. Expected 'geometry' or 'gdal'."),
                    };
                    break;
                case "--output" or "-o":
                    outputDir = NextValue(args, ref i, "--output");
                    break;
                default:
                    throw new GpCliUsageException($"Unknown option '{args[i]}'.");
            }
        }

        if (!GpScaffolder.TryValidateProcessId(processId, out var validationError))
        {
            throw new GpCliUsageException($"Invalid process id: {validationError}");
        }

        // Reuse the live P1 registration as the collision source of truth: the runner's
        // available ids are exactly the registered IProcessExecutor set.
        using var provider = BuildProvider();
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GeoprocessingLocalRunner(executors);
        var existingIds = runner.AvailableProcessIds;

        GpScaffoldPlan plan;
        try
        {
            plan = GpScaffolder.Plan(processId, kind, existingIds);
        }
        catch (InvalidOperationException ex)
        {
            throw new GpCliUsageException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new GpCliUsageException(ex.Message);
        }

        if (outputDir is not null)
        {
            // Preview mode: write every rendered file under <outputDir>, untouched repo.
            Console.WriteLine($"Previewing scaffold for '{processId}' ({plan.Kind}) under {outputDir}:");
            foreach (var file in plan.Files)
            {
                var target = Path.Combine(outputDir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Contents).ConfigureAwait(false);
                Console.WriteLine($"  wrote {file.RelativePath}  ({file.Description})");
            }

            Console.WriteLine();
            Console.WriteLine("Preview only — no repo files were modified. To scaffold in place, omit --output.");
            Console.WriteLine();
            Console.WriteLine(plan.NextSteps);
            return 0;
        }

        // In-place mode: write the executor + fixture files at their real locations and
        // inject the one-line DI registration + catalog entry so the process is REGISTERED.
        var repoRoot = ResolveRepoRoot();
        if (repoRoot is null)
        {
            throw new GpCliUsageException(
                "Could not locate the repository root (no Honua.sln found walking up). "
                + "Run from inside the checkout, or use --output <dir> to preview the files instead.");
        }

        Console.WriteLine($"Scaffolding '{processId}' ({plan.Kind}) in {repoRoot}:");
        foreach (var file in plan.Files)
        {
            var target = Path.Combine(repoRoot, file.RelativePath);
            if (File.Exists(target))
            {
                throw new GpCliUsageException(
                    $"Refusing to overwrite existing file {file.RelativePath}. "
                    + "Remove it first or pick a different id.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Contents).ConfigureAwait(false);
            Console.WriteLine($"  wrote {file.RelativePath}");
        }

        await InjectRegistrationAndCatalogAsync(repoRoot, processId, plan.Kind).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(plan.NextSteps);
        return 0;
    }

    private static async Task<int> RunPublish(string[] args)
    {
        using var provider = BuildProvider();
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GeoprocessingLocalRunner(executors);
        var processIds = runner.AvailableProcessIds.ToHashSet(StringComparer.Ordinal);
        var catalog = provider.GetService<IProcessCatalog>();

        return await GpPublishCommand.RunAsync(
            args,
            processIds,
            catalog,
            CreatePublishClient,
            Console.Out,
            Console.Error).ConfigureAwait(false);
    }

    /// <summary>
    /// Default REST-client factory for the publish verb: a real <see cref="HttpClient"/> rooted at
    /// the <c>--server</c> base URL, authenticated with the admin API key (or a bearer token).
    /// </summary>
    private static WorkflowPackagePublishClient CreatePublishClient(GpPublishOptions options)
    {
        if (!Uri.TryCreate(options.Server, UriKind.Absolute, out var baseAddress))
        {
            throw new GpCliUsageException($"Invalid --server URL: '{options.Server}'.");
        }

        var http = new HttpClient { BaseAddress = baseAddress };
        return new WorkflowPackagePublishClient(http, options.ApiKey, options.BearerToken);
    }

    /// <summary>
    /// Injects the DI registration line and the catalog <c>ProcessDefinition</c> for the new
    /// process so it is registered + plannable. Reports (rather than fails) when an anchor is
    /// missing so the author can wire the one line by hand from the printed note.
    /// </summary>
    private static async Task InjectRegistrationAndCatalogAsync(
        string repoRoot,
        string processId,
        GpProcessKind kind)
    {
        var className = GpScaffolder.ToTypeStem(processId)
            + (kind == GpProcessKind.Gdal ? "NativeJobExecutor" : "JobExecutor");
        var registrationCall = kind == GpProcessKind.Gdal
            ? $"RegisterGdalExecutor<{className}>(services);"
            : $"Register<{className}>(services);";
        var registrationFile = Path.Combine(
            repoRoot,
            kind == GpProcessKind.Gdal
                ? "src/Honua.Worker.Gdal/GdalWorkerServiceCollectionExtensions.cs"
                : "src/Honua.Geoprocessing/Features/Geoprocessing/GeoprocessingServiceCollectionExtensions.cs");

        if (File.Exists(registrationFile))
        {
            var source = await File.ReadAllTextAsync(registrationFile).ConfigureAwait(false);
            if (GpScaffoldInjector.TryInsertRegistration(source, registrationCall, out var updated, out var error))
            {
                await File.WriteAllTextAsync(registrationFile, updated).ConfigureAwait(false);
                Console.WriteLine($"  registered {className} in {Path.GetFileName(registrationFile)}");
            }
            else
            {
                Console.WriteLine($"  NOTE: could not auto-register ({error}). Add '{registrationCall}' by hand.");
            }
        }

        // The catalog lives in Honua.Geoprocessing for both kinds (it is the managed-side
        // metadata surface; native processes still declare a definition with the native
        // runtime profile).
        var catalogFile = Path.Combine(
            repoRoot,
            "src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs");
        if (File.Exists(catalogFile))
        {
            var source = await File.ReadAllTextAsync(catalogFile).ConfigureAwait(false);
            if (GpScaffoldInjector.TryInsertCatalogEntry(source, processId, kind, out var updated, out var error))
            {
                await File.WriteAllTextAsync(catalogFile, updated).ConfigureAwait(false);
                Console.WriteLine($"  catalogued {processId} in BuiltInProcessCatalog.cs");
            }
            else
            {
                Console.WriteLine($"  NOTE: could not auto-catalog ({error}). Add a ProcessDefinition by hand.");
            }
        }
    }

    /// <summary>
    /// Resolves the repository root by walking up from the current directory looking for
    /// <c>Honua.sln</c>, so the scaffolder writes files to the right place regardless of the
    /// working directory inside the checkout.
    /// </summary>
    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Honua.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Resolves the GP fixtures root: the explicit <c>--root</c> when supplied, else the
    /// first <c>samples/gp</c> found by walking up from the current directory (so the tool
    /// works from anywhere in a checkout).
    /// </summary>
    private static string? ResolveFixtureRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
        {
            return Directory.Exists(explicitRoot) ? Path.GetFullPath(explicitRoot) : null;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "gp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private readonly record struct ProcessArgs(
        string ProcessId,
        string? InputPath,
        string? OutPath,
        bool GlassBoxRequested,
        Dictionary<string, string> Params);

    /// <summary>
    /// Resolves the parameter name that <c>--input &lt;file&gt;</c> binds to: the
    /// first required file-like (<c>Wkb</c>/<c>WkbArray</c>/<c>Text</c>) parameter the
    /// process declares, or <c>null</c> when the process has none.
    /// </summary>
    private static string? ResolveFileInputKey(ProcessDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }

        foreach (var parameter in definition.Parameters)
        {
            if (parameter.Required
                && parameter.ValueType is ProcessParameterValueType.Wkb
                    or ProcessParameterValueType.WkbArray
                    or ProcessParameterValueType.Text)
            {
                return parameter.Name;
            }
        }

        return null;
    }

    private static byte[] DecodeArtifact(string artifact)
    {
        // Executors publish artifacts as base64 data URIs (data:<media>;base64,<payload>).
        var comma = artifact.IndexOf(',', StringComparison.Ordinal);
        if (artifact.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
        {
            return Convert.FromBase64String(artifact[(comma + 1)..]);
        }

        // Non-data-URI artifact references (e.g. file/object paths) are returned as text.
        return System.Text.Encoding.UTF8.GetBytes(artifact);
    }

    private static string Summarize(string artifact)
    {
        if (artifact.Length <= 80)
        {
            return artifact;
        }

        return artifact[..77] + "...";
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new GpCliUsageException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }

    private static int PrintUsageAndOk()
    {
        PrintUsage();
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("honua gp — headless, Redis-free geoprocessing dev runner (GP Devkit)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  honua gp list");
        Console.WriteLine("  honua gp describe <processId> [--json]");
        Console.WriteLine("  honua gp plan <processId> [--input <file>] [--param k=v ...]");
        Console.WriteLine("  honua gp run  <processId> [--input <file>] [--param k=v ...] [--out <file>] [--glass-box]");
        Console.WriteLine("  honua gp test [<fixtureId>] [--root <dir>] [--update]");
        Console.WriteLine("  honua gp new <id> [--kind geometry|gdal] [--output <dir>]");
        Console.WriteLine("  honua gp publish <id> [--server <url>] [--api-key <key>] [--message <m>] [--dry-run] [--publish]");
        Console.WriteLine();
        Console.WriteLine("Run options:");
        Console.WriteLine("  --input, -i <file>  Read a file and bind it (base64) to the process's primary input.");
        Console.WriteLine("  --param, -p k=v     Set a step-0 input (repeatable). Overrides --input for the same key.");
        Console.WriteLine("  --out,   -o <file>  Write the first published artifact's bytes to <file> (run only).");
        Console.WriteLine("  --root,  -r <dir>   Golden fixtures directory (default: nearest samples/gp).");
        Console.WriteLine("  --update,-u         Regenerate goldens from the produced artifacts (also via HONUA_GP_UPDATE_GOLDENS).");
        Console.WriteLine("  --kind,  -k <k>     'geometry' (managed, default) or 'gdal' (native) scaffold for `new`.");
        Console.WriteLine("  --output,-o <dir>   Preview the scaffold under <dir> instead of writing into the checkout.");
        Console.WriteLine("  --glass-box, --debug, -d  DEV-ONLY (run): show the unsanitized native command(s),");
        Console.WriteLine("                      full stdout/stderr, scratch dirs, and a repro hint (also HONUA_GP_GLASSBOX).");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  describe  Show a process's typed parameters, outputs, inputs JSON Schema, and an example (--json for the machine view).");
        Console.WriteLine("  plan  Dry-run: validate params + DAG and estimate output size/cost WITHOUT executing.");
        Console.WriteLine("  run   Execute the process and emit its artifact(s).");
        Console.WriteLine("  test  Run golden-file fixtures under samples/gp (or --root) and assert outputs.");
        Console.WriteLine("  publish  Push an authored workflow to the WorkflowPackage store (GitOps approval-gated promotion).");
        Console.WriteLine();
        Console.WriteLine("Publish options:");
        Console.WriteLine("  --server, -s <url>  Console REST server root. Default http://localhost:8080.");
        Console.WriteLine("  --api-key, -k <key> Admin API key (X-API-Key). Defaults to $HONUA_ADMIN_PASSWORD.");
        Console.WriteLine("  --bearer <token>    Bearer token instead of an API key.");
        Console.WriteLine("  --message, -m <m>   Author message stamped on the package.");
        Console.WriteLine("  --file, -f <json>   Publish an explicit WorkflowGraph JSON file.");
        Console.WriteLine("  --as-process-node   Wrap a single registered process as a one-node workflow package.");
        Console.WriteLine("  --dry-run           Validate + dry-run only; never create a publication.");
        Console.WriteLine("  --publish           Also publish the created version to a process endpoint.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  honua gp describe geometry.buffer          # typed params, outputs, JSON Schema, example");
        Console.WriteLine("  honua gp describe geometry.buffer --json   # machine-readable descriptor");
        Console.WriteLine("  honua gp plan geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run gdal.ogr2ogr --input in.geojson --param sourceFormat=GeoJSON --param targetFormat=CSV --out out.csv");
        Console.WriteLine("  honua gp test                       # run every golden fixture under samples/gp");
        Console.WriteLine("  honua gp test geometry-buffer-point # run one fixture by id");
        Console.WriteLine("  honua gp test --update              # regenerate goldens after an intended change");
        Console.WriteLine("  honua gp new geometry.recenter      # scaffold a registered, runnable, golden-tested process");
        Console.WriteLine("  honua gp new gdal.warp-clip --kind gdal --output /tmp/preview  # preview only");
        Console.WriteLine("  honua gp publish my-flow --file workflow.json --dry-run");
        Console.WriteLine("  honua gp publish geometry.buffer --as-process-node --publish --api-key <key>");
    }
}

/// <summary>
/// Signals a CLI usage error that should print the message plus usage and exit
/// non-zero, distinct from an executor-reported run failure.
/// </summary>
internal sealed class GpCliUsageException(string message) : Exception(message);
