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

    private static async Task<int> RunRun(string[] args)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException("Missing <processId>. Usage: honua gp run <id> --input <file> [--param k=v] [--out <file>]");
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

        // Glass-box (dev-only) is gated behind an EXPLICIT opt-in: the --glass-box/--debug
        // flag OR the HONUA_GP_GLASSBOX env truthy. Absent both, the run takes the default
        // sanitized path (no raw scratch paths / no untruncated stderr) — the same output a
        // production-equivalent caller sees.
        var glassBoxEnabled = glassBoxRequested || IsEnvTruthy("HONUA_GP_GLASSBOX");
        var capture = glassBoxEnabled ? new GlassBoxCapture() : null;

        using var provider = BuildProvider(capture);
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

        var result = await runner.RunAsync(processId, inputs, capture).ConfigureAwait(false);

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

    private static bool IsEnvTruthy(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
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
        Console.WriteLine("  honua gp run <processId> [--input <file>] [--param k=v ...] [--out <file>] [--glass-box]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input, -i <file>  Read a file and bind it (base64) to the process's primary input.");
        Console.WriteLine("  --param, -p k=v     Set a step-0 input (repeatable). Overrides --input for the same key.");
        Console.WriteLine("  --out,   -o <file>  Write the first published artifact's bytes to <file>.");
        Console.WriteLine("  --glass-box, --debug, -d  DEV-ONLY: make the box transparent — show the");
        Console.WriteLine("                      UNSANITIZED GDAL command (real scratch paths), full");
        Console.WriteLine("                      stdout/stderr, a phase timeline, an artifact preview, and a");
        Console.WriteLine("                      'repro locally' hint. Also enabled by HONUA_GP_GLASSBOX=1.");
        Console.WriteLine("                      Without it, the run stays on the sanitized (prod) path.");
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
