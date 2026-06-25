// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.LocalRunner;
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
                "run" => await RunRun(rest).ConfigureAwait(false),
                "plan" => await RunPlan(rest).ConfigureAwait(false),
                "-h" or "--help" or "help" => PrintUsageAndOk(),
                _ => Fail($"Unknown command '{verb}'."),
            };
        }
        catch (GpCliUsageException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static ServiceProvider BuildProvider()
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

    private static async Task<int> RunRun(string[] args)
    {
        var parsed = ParseProcessArgs(
            args,
            "honua gp run <id> --input <file> [--param k=v] [--out <file>]");

        using var provider = BuildProvider();
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

        var result = await runner.RunAsync(parsed.ProcessId, inputs).ConfigureAwait(false);

        Console.WriteLine($"process : {result.ProcessId}");
        Console.WriteLine($"status  : {result.Status}");
        Console.WriteLine($"elapsed : {result.Elapsed.TotalMilliseconds:F1} ms");

        foreach (var log in result.Logs)
        {
            if (log.Metadata is not null
                && log.Metadata.TryGetValue("gdal.command", out var command))
            {
                Console.WriteLine($"command : {command}");
            }
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning : {warning}");
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

        return new ProcessArgs(processId, inputPath, outPath, inputs);
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

    private readonly record struct ProcessArgs(
        string ProcessId,
        string? InputPath,
        string? OutPath,
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
        Console.WriteLine("  honua gp plan <processId> [--input <file>] [--param k=v ...]");
        Console.WriteLine("  honua gp run  <processId> [--input <file>] [--param k=v ...] [--out <file>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input, -i <file>  Read a file and bind it (base64) to the process's primary input.");
        Console.WriteLine("  --param, -p k=v     Set a step-0 input (repeatable). Overrides --input for the same key.");
        Console.WriteLine("  --out,   -o <file>  Write the first published artifact's bytes to <file> (run only).");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  plan  Dry-run: validate params + DAG and estimate output size/cost WITHOUT executing.");
        Console.WriteLine("  run   Execute the process and emit its artifact(s).");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  honua gp plan geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run gdal.ogr2ogr --input in.geojson --param sourceFormat=GeoJSON --param targetFormat=CSV --out out.csv");
    }
}

/// <summary>
/// Signals a CLI usage error that should print the message plus usage and exit
/// non-zero, distinct from an executor-reported run failure.
/// </summary>
internal sealed class GpCliUsageException(string message) : Exception(message);
