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
                "-h" or "--help" or "help" => PrintUsageAndOk(),
                _ => Fail($"Unknown command '{verb}'."),
            };
        }
        catch (GpCliUsageException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static ServiceProvider BuildProvider(GdalProcessExecutorMode gdalMode = GdalProcessExecutorMode.InProcess)
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
        // In container mode (--real-worker, #2180) each native gdal.* step runs inside
        // the real honua-worker-etl image so gp run/plan is a true dry-run of the
        // native submit path; the managed executor set and the durable spec are
        // unchanged — only the GDAL command runner differs.
        services.AddGdalProcessExecutors(configuration, gdalMode);

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
        if (args.Length == 0)
        {
            throw new GpCliUsageException("Missing <processId>. Usage: honua gp run <id> --input <file> [--param k=v] [--out <file>]");
        }

        var processId = args[0];
        string? inputPath = null;
        string? outPath = null;
        // null = auto: use the container path when the worker image is present locally,
        // else the fast in-process path. true = force container, false = force in-process.
        bool? forceContainer = null;
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
                case "--container" or "--real-worker":
                    forceContainer = true;
                    break;
                case "--in-process":
                    forceContainer = false;
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

        var gdalMode = await ResolveGdalModeAsync(forceContainer).ConfigureAwait(false);
        using var provider = BuildProvider(gdalMode);
        var executors = provider.GetServices<IProcessExecutor>();
        var runner = new GeoprocessingLocalRunner(executors);

        if (!runner.AvailableProcessIds.Contains(processId, StringComparer.Ordinal))
        {
            throw new GpCliUsageException(
                $"Process id '{processId}' is not registered. Run 'honua gp list' to see available processes.");
        }

        // Bind --input <file>: read the file, base64-encode its bytes, and assign it
        // to the process's primary file-like input (the first required Wkb/Text/WkbArray
        // parameter), unless the caller already supplied that input via --param.
        if (inputPath is not null)
        {
            if (!File.Exists(inputPath))
            {
                throw new GpCliUsageException($"--input file not found: {inputPath}");
            }

            var catalog = provider.GetService<IProcessCatalog>();
            var inputKey = ResolveFileInputKey(catalog?.GetProcess(processId));
            if (inputKey is null)
            {
                throw new GpCliUsageException(
                    $"Process '{processId}' has no file-like input to bind --input to; supply inputs with --param instead.");
            }

            if (!inputs.ContainsKey(inputKey))
            {
                inputs[inputKey] = Convert.ToBase64String(await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false));
            }
        }

        var result = await runner.RunAsync(processId, inputs).ConfigureAwait(false);

        Console.WriteLine($"process : {result.ProcessId}");
        Console.WriteLine($"runner  : {(gdalMode == GdalProcessExecutorMode.Container ? "container (honua-worker-etl image)" : "in-process")}");
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

        if (outPath is not null)
        {
            if (result.Artifacts.Count == 0)
            {
                Console.Error.WriteLine("error   : run succeeded but produced no artifact to write to --out.");
                return 1;
            }

            var bytes = DecodeArtifact(result.Artifacts[0]);
            await File.WriteAllBytesAsync(outPath, bytes).ConfigureAwait(false);
            Console.WriteLine($"wrote   : {outPath} ({bytes.Length} bytes)");
        }

        return 0;
    }

    /// <summary>
    /// Resolves which GDAL command-runner mode <c>gp run</c> uses for native ops
    /// (issue #2180), honoring an explicit <c>--real-worker</c>/<c>--in-process</c>
    /// override and otherwise auto-selecting the container path only when the worker
    /// image is already present locally (so the managed sub-second loop is never
    /// blocked on a pull).
    /// <list type="bullet">
    /// <item><c>--real-worker</c> (force container): fails fast when the worker image is
    /// absent so a developer who asked for full fidelity is not silently downgraded.</item>
    /// <item><c>--in-process</c> (force fast): always the host CLIs.</item>
    /// <item>default (auto): container when the image is available, else in-process.</item>
    /// </list>
    /// </summary>
    private static async Task<GdalProcessExecutorMode> ResolveGdalModeAsync(bool? forceContainer)
    {
        if (forceContainer == false)
        {
            return GdalProcessExecutorMode.InProcess;
        }

        var imageAvailable = await GdalContainerProbe.IsImageAvailableAsync().ConfigureAwait(false);

        if (forceContainer == true)
        {
            if (!imageAvailable)
            {
                throw new GpCliUsageException(
                    $"--real-worker requires the '{GdalContainerProbe.DefaultImage}' worker image, which is not " +
                    "available in the local container runtime. Build it with "
                    + "'docker build -f docker/worker-gdal/Dockerfile -t honua-worker-etl .', or drop "
                    + "--real-worker to use the fast in-process path.");
            }

            return GdalProcessExecutorMode.Container;
        }

        // Auto: prefer the real-worker fidelity path when the image is already present.
        return imageAvailable ? GdalProcessExecutorMode.Container : GdalProcessExecutorMode.InProcess;
    }

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
        Console.WriteLine("  honua gp run <processId> [--input <file>] [--param k=v ...] [--out <file>] [--real-worker]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input, -i <file>  Read a file and bind it (base64) to the process's primary input.");
        Console.WriteLine("  --param, -p k=v     Set a step-0 input (repeatable). Overrides --input for the same key.");
        Console.WriteLine("  --out,   -o <file>  Write the first published artifact's bytes to <file>.");
        Console.WriteLine("  --real-worker       Run native gdal.* steps inside the real honua-worker-etl");
        Console.WriteLine("  (--container)       image (docker run) instead of the host GDAL CLIs, so the run");
        Console.WriteLine("                      crosses the same image/CRS/arg boundary a native submit does.");
        Console.WriteLine("                      Default: auto (container when the image is present, else in-process).");
        Console.WriteLine("  --in-process        Force the fast host-CLI path even when the worker image is present.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  honua gp run geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run gdal.ogr2ogr --input in.geojson --param sourceFormat=GeoJSON --param targetFormat=CSV --out out.csv");
    }
}

/// <summary>
/// Signals a CLI usage error that should print the message plus usage and exit
/// non-zero, distinct from an executor-reported run failure.
/// </summary>
internal sealed class GpCliUsageException(string message) : Exception(message);
