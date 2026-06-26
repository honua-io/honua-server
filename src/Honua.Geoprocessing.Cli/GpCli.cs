// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.LocalRunner;
using Honua.Geoprocessing.Testing;
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
                "test" => await RunTest(rest).ConfigureAwait(false),
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
        if (args.Length == 0)
        {
            throw new GpCliUsageException("Missing <processId>. Usage: honua gp run <id> --input <file> [--param k=v] [--out <file>]");
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

        using var provider = BuildProvider();
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
        Console.WriteLine("  honua gp run <processId> [--input <file>] [--param k=v ...] [--out <file>]");
        Console.WriteLine("  honua gp test [<fixtureId>] [--root <dir>] [--update]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input, -i <file>  Read a file and bind it (base64) to the process's primary input.");
        Console.WriteLine("  --param, -p k=v     Set a step-0 input (repeatable). Overrides --input for the same key.");
        Console.WriteLine("  --out,   -o <file>  Write the first published artifact's bytes to <file>.");
        Console.WriteLine("  --root,  -r <dir>   Golden fixtures directory (default: nearest samples/gp).");
        Console.WriteLine("  --update,-u         Regenerate goldens from the produced artifacts (also via HONUA_GP_UPDATE_GOLDENS).");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  honua gp run geometry.buffer --param wkb=<base64> --param srid=4326 --param distance=10");
        Console.WriteLine("  honua gp run gdal.ogr2ogr --input in.geojson --param sourceFormat=GeoJSON --param targetFormat=CSV --out out.csv");
        Console.WriteLine("  honua gp test                       # run every golden fixture under samples/gp");
        Console.WriteLine("  honua gp test geometry-buffer-point # run one fixture by id");
        Console.WriteLine("  honua gp test --update              # regenerate goldens after an intended change");
    }
}

/// <summary>
/// Signals a CLI usage error that should print the message plus usage and exit
/// non-zero, distinct from an executor-reported run failure.
/// </summary>
internal sealed class GpCliUsageException(string message) : Exception(message);
