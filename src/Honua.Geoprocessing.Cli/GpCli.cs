// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Cli.Publish;
using Honua.Geoprocessing.Cli.Release;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.LocalRunner;
using Honua.Geoprocessing.Testing;
using Honua.Worker.Gdal;
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
    /// Parses argv and dispatches to a devkit verb (<c>list</c>, <c>run</c>, <c>reproduce</c>, and the others in <see cref="PrintUsage"/>).
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
                "list" => RunList(rest),
                "doctor" => await RunDoctor(rest).ConfigureAwait(false),
                "describe" => RunDescribe(rest),
                "run" => await RunRun(rest).ConfigureAwait(false),
                "test" => await RunTest(rest).ConfigureAwait(false),
                "plan" => await RunPlan(rest).ConfigureAwait(false),
                "new" => await RunNew(rest).ConfigureAwait(false),
                "init" => RunInit(rest),
                "publish" => await RunPublish(rest).ConfigureAwait(false),
                "release" => GpReleaseCommand.Run(rest, Console.Out, Console.Error),
                "reproduce" => await GpReproduceCommand.RunAsync(rest, Console.Out, Console.Error).ConfigureAwait(false),
                "-h" or "--help" or "help" => PrintUsageAndOk(),
                _ => Fail($"Unknown command '{verb}'."),
            };
        }
        catch (GpCliUsageException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static ServiceProvider BuildProvider(
        GdalProcessExecutorMode gdalMode = GdalProcessExecutorMode.InProcess,
        GlassBoxCapture? glassBox = null,
        string? scratchRoot = null)
    {
        // Only the dev container settings are configurable; no Redis or control-plane
        // services are composed. Native bind mounts use the invoking developer's UID/GID.
        // reproduce passes a fresh scratch root and never a path from the bundle.
        var configuration = GpContainerConfiguration.Create(scratchRoot: scratchRoot);

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

    /// <summary>
    /// Resolves the registered <see cref="IProcessExecutor"/> set for the local runner,
    /// de-duplicating any process id claimed by BOTH a managed executor and a native GDAL
    /// executor. In the real system the managed executors (server host) and the native GDAL
    /// executors (worker host) run in SEPARATE processes, so an id handled by both — e.g.
    /// <c>transform.reproject</c> (managed NTS reproject vs native <c>ogr2ogr</c>) — never
    /// collides there. The devkit composes BOTH sets into one provider for a single offline
    /// loop, so the overlap is resolved deterministically here: the managed executor wins (the
    /// sub-second, binary-free default), and a native executor is dropped only when EVERY id it
    /// handles is already owned by a managed executor. Its distinct <c>gdal.*</c> ids and the
    /// <c>--real-worker</c> fidelity path are unaffected; a partial overlap is left for
    /// <see cref="ProcessExecutorRouteTable"/> to surface as the authoring error it is.
    /// </summary>
    private static List<IProcessExecutor> ResolveExecutors(ServiceProvider provider)
    {
        var all = provider.GetServices<IProcessExecutor>().ToList();
        var gdalAssembly = typeof(GdalProcessExecutorMode).Assembly;

        var managed = all.Where(e => e.GetType().Assembly != gdalAssembly).ToList();
        var claimed = new HashSet<string>(
            managed.SelectMany(e => e.ProcessIds), StringComparer.Ordinal);

        var resolved = new List<IProcessExecutor>(managed);
        foreach (var executor in all.Where(e => e.GetType().Assembly == gdalAssembly))
        {
            if (executor.ProcessIds.All(claimed.Contains))
            {
                // Pure managed/native overlap (e.g. transform.reproject): managed already owns it.
                continue;
            }

            resolved.Add(executor);
        }

        return resolved;
    }

    /// <summary>
    /// One in-process local run against a scratch directory the caller created.
    /// Same executor set as <c>honua gp run</c>, without a receipt directory, glass box,
    /// or container probe.
    /// </summary>
    internal static async Task<LocalRunResult> ExecuteLocalAsync(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        string scratchRoot,
        CancellationToken cancellationToken)
    {
        using var provider = BuildProvider(GdalProcessExecutorMode.InProcess, scratchRoot: scratchRoot);
        var executors = ResolveExecutors(provider);
        var runner = new GeoprocessingLocalRunner(executors);
        if (!runner.AvailableProcessIds.Contains(processId, StringComparer.Ordinal))
        {
            return LocalRunResult.UnknownProcess(processId, string.Join(", ", runner.AvailableProcessIds));
        }

        return await runner.RunAsync(processId, inputs, cancellationToken).ConfigureAwait(false);
    }

    private static int RunList(string[] args)
    {
        string? project = null;
        var asJson = false;
        var probeContainer = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "--probe-container":
                    probeContainer = true;
                    break;
                case "--project":
                    project = NextValue(args, ref i, "--project");
                    break;
                default:
                    throw new GpCliUsageException("Usage: honua-gp-devkit list [--json] [--probe-container] [--project <dir>]");
            }
        }

        using var provider = BuildProvider();
        var executors = ResolveExecutors(provider);
        var runner = new GeoprocessingLocalRunner(executors);
        var catalog = provider.GetService<IProcessCatalog>();
        var probe = BuildProbe(probeContainer);
        (GpProjectManifest Manifest, GpRunnability Verdict)? projectDiscovery = project is null ? null : DiscoverProject(project);

        if (asJson)
        {
            var definitions = new JsonArray();
            foreach (var id in runner.AvailableProcessIds)
            {
                var definition = catalog?.GetProcess(id);
                var runnability = GpRunnabilityAssessor.Assess(definition, id, ExecutorProfile(executors, id), probe);
                if (definition is null)
                {
                    var bare = new JsonObject { ["id"] = id, ["dependenciesVerified"] = false };
                    runnability.WriteTo(bare);
                    definitions.Add(bare);
                }
                else
                {
                    definitions.Add(JsonNode.Parse(GpDescribeRenderer.RenderJson(definition, runnability)));
                }
            }

            if (projectDiscovery is { } discovered)
            {
                definitions.Add(GpProjectDiscovery.WriteDescriptor(discovered.Manifest, discovered.Verdict));
            }

            Console.WriteLine(definitions.ToJsonString());
            return 0;
        }

        Console.WriteLine("Available geoprocessing processes:");
        Console.WriteLine("  (registered; [mode] or [mode, blocked: <prerequisite>] says whether it can run here)");
        foreach (var id in runner.AvailableProcessIds)
        {
            var def = catalog?.GetProcess(id);
            var runnability = GpRunnabilityAssessor.Assess(def, id, ExecutorProfile(executors, id), probe);
            var verdict = runnability.RunnableHere ? runnability.ExecutionModeLabel : $"{runnability.ExecutionModeLabel}, blocked: {runnability.BlockedBy}";
            Console.WriteLine(def is not null ? $"  {id,-32} {def.Title,-48} [{verdict}]" : $"  {id,-32} {"",-48} [{verdict}]");
        }

        if (projectDiscovery is { } projectVerdict)
        {
            var label = projectVerdict.Verdict.RunnableHere
                ? projectVerdict.Verdict.ExecutionModeLabel
                : $"{projectVerdict.Verdict.ExecutionModeLabel}, blocked: {projectVerdict.Verdict.BlockedBy}";
            Console.WriteLine();
            Console.WriteLine("Custom project (local; not a catalog registration):");
            Console.WriteLine($"  {projectVerdict.Manifest.Name,-32} {projectVerdict.Manifest.Entrypoint,-48} [{label}]");
        }

        return 0;
    }

    /// <summary>The runtime profile the registered executor for <paramref name="processId"/> accepts.</summary>
    private static string? ExecutorProfile(IEnumerable<IProcessExecutor> executors, string processId)
    {
        var executor = executors.FirstOrDefault(e => e.ProcessIds.Contains(processId, StringComparer.Ordinal));
        if (executor is null)
        {
            return null;
        }

        return executor.AcceptedRuntimeProfiles.Contains(RuntimeProfiles.Native) ? RuntimeProfiles.Native
            : executor.AcceptedRuntimeProfiles.Contains(CustomCodeJobContract.RuntimeProfile) ? CustomCodeJobContract.RuntimeProfile
            : null;
    }

    /// <summary>
    /// The machine facts runnability is judged against. The container probe shells out to
    /// the container runtime, so it runs only on request (or when a run already resolved it).
    /// </summary>
    private static GpEnvironmentProbe BuildProbe(bool probeContainer, bool? knownImageAvailability = null)
    {
        var configuration = GpContainerConfiguration.Create();
        var image = configuration["GdalContainer:Image"]!;
        if (knownImageAvailability is { } known)
        {
            return new GpEnvironmentProbe(known, image);
        }

        if (!probeContainer)
        {
            return new GpEnvironmentProbe(null, image);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool available;
        try
        {
            available = GdalContainerProbe.IsImageAvailableAsync(image, configuration["GdalContainer:DockerExecutable"]!, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            available = false;
        }

        return new GpEnvironmentProbe(available, image);
    }

    private static async Task<int> RunDoctor(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg is not ("--json" or "--managed-only"))
            {
                throw new GpCliUsageException($"Unknown option '{arg}'.");
            }
        }

        using var provider = BuildProvider();
        var runner = new GeoprocessingLocalRunner(ResolveExecutors(provider));
        // Exercise an actual executor: registration alone cannot prove local readiness.
        var probe = await runner.RunAsync("geometry.buffer", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wkb"] = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["srid"] = "3857",
            ["distance"] = "10",
        }).ConfigureAwait(false);
        var configuration = GpContainerConfiguration.Create();
        var managedOnly = args.Contains("--managed-only", StringComparer.Ordinal);
        var image = configuration["GdalContainer:Image"]!;
        var nativeReady = false;
        if (!managedOnly)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                nativeReady = await GdalContainerProbe.IsImageAvailableAsync(
                    image, configuration["GdalContainer:DockerExecutable"]!, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                nativeReady = false;
            }
        }

        // Ports are the defaults published by docker-compose.gp-dev.yml
        // (POSTGRES_PORT, REDIS_PORT, HONUA_HTTP_PORT, HONUA_GRPC_PORT).
        const string integrationProfile =
            "integration profile: Docker Compose v2; free host ports 5432 (PostGIS), 6379 (Redis), 8080 (HTTP) and 8081 (gRPC), then run docker compose -f docker-compose.gp-dev.yml --profile integration up --build -d.";
        var report = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["managed"] = probe.Succeeded ? "ready" : "failed",
            ["registeredProcessCount"] = runner.AvailableProcessIds.Count,
            ["nativeWorker"] = managedOnly ? "notChecked" : nativeReady ? "imageAvailable" : "unavailable",
            ["workerImage"] = image,
            ["serviceBacked"] = "requiresIntegrationStack",
            ["integrationProfile"] = integrationProfile,
            ["note"] = "Image availability is a prerequisite check; use run --real-worker to verify native execution. Service-backed processes require server/database configuration.",
        };
        GpHarnessPrerequisiteProbe.WriteJson(report);
        var toolchain = GpHarnessPrerequisiteProbe.FormatReport();
        Console.WriteLine(args.Contains("--json", StringComparer.Ordinal)
            ? report.ToJsonString()
            : $"managed: {report["managed"]}\nnative worker: {report["nativeWorker"]} ({image})\n{integrationProfile}\n{report["note"]}\n{toolchain}");
        // Toolchain presence is reported only. It is not part of this exit code.
        return probe.Succeeded && (managedOnly || nativeReady) ? 0 : 1;
    }

    private static int RunDescribe(string[] args)
    {
        if (args.Length == 0)
        {
            throw new GpCliUsageException(
                "Missing <processId>. Usage: honua gp describe <processId> [--json] [--project <dir>]");
        }

        string? processId = null;
        string? project = null;
        var asJson = false;
        var probeContainer = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" or "-j":
                    asJson = true;
                    break;
                case "--probe-container":
                    probeContainer = true;
                    break;
                case "--project":
                    project = NextValue(args, ref i, "--project");
                    break;
                default:
                    if (args[i].StartsWith('-') || processId is not null)
                    {
                        throw new GpCliUsageException($"Unknown option '{args[i]}'. Usage: honua gp describe <processId> [--json] [--project <dir>]");
                    }

                    processId = args[i];
                    break;
            }
        }

        if (project is not null)
        {
            if (processId is not null)
            {
                throw new GpCliUsageException("describe takes a process id or --project <dir>, not both.");
            }

            var discovered = DiscoverProject(project);
            Console.WriteLine(asJson
                ? GpProjectDiscovery.WriteDescriptor(discovered.Manifest, discovered.Verdict).ToJsonString()
                : GpProjectDiscovery.RenderText(discovered.Manifest, discovered.Verdict));
            return 0;
        }

        if (processId is null)
        {
            throw new GpCliUsageException(
                "Missing <processId>. Usage: honua gp describe <processId> [--json] [--project <dir>]");
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

        var runnability = GpRunnabilityAssessor.Assess(definition, processId, ExecutorProfile(ResolveExecutors(provider), processId), BuildProbe(probeContainer));
        Console.WriteLine(asJson
            ? GpDescribeRenderer.RenderJson(definition, runnability)
            : GpDescribeRenderer.RenderText(definition, runnability));
        return 0;
    }

    private static async Task<int> RunRun(string[] args)
    {
        var parsed = ParseProcessArgs(
            args,
            "honua gp run <id> --input <file> [--param k=v] [--out <file>] [--receipt-dir <dir> | --no-receipt]");

        // Glass-box (dev-only) is gated behind an EXPLICIT opt-in: the --glass-box/--debug
        // flag OR the HONUA_GP_GLASSBOX env truthy. Absent both, the run takes the default
        // sanitized path (no raw scratch paths / no untruncated stderr) — the same output a
        // production-equivalent caller sees.
        var glassBoxEnabled = parsed.GlassBoxRequested || IsEnvTruthy("HONUA_GP_GLASSBOX");
        var capture = glassBoxEnabled ? new GlassBoxCapture() : null;

        var gdalMode = await ResolveGdalModeAsync(parsed.ForceContainer).ConfigureAwait(false);
        using var provider = BuildProvider(gdalMode, capture);
        var executors = ResolveExecutors(provider);
        var runner = new GeoprocessingLocalRunner(executors);

        if (!runner.AvailableProcessIds.Contains(parsed.ProcessId, StringComparer.Ordinal))
        {
            throw new GpCliUsageException(
                $"Process id '{parsed.ProcessId}' is not registered. Run 'honua gp list' to see available processes.");
        }

        var inputs = new Dictionary<string, string>(parsed.Params, StringComparer.Ordinal);
        var catalog = provider.GetService<IProcessCatalog>();
        await BindFileInputAsync(parsed.ProcessId, parsed.InputPath, catalog, inputs).ConfigureAwait(false);

        // Registered is not runnable. A function whose prerequisite is missing fails here,
        // naming it; the devkit never turns a local run into a server submission.
        var runnability = GpRunnabilityAssessor.Assess(
            catalog?.GetProcess(parsed.ProcessId), parsed.ProcessId, ExecutorProfile(executors, parsed.ProcessId),
            BuildProbe(probeContainer: false, knownImageAvailability: gdalMode == GdalProcessExecutorMode.Container ? true : null));
        if (!runnability.RunnableHere)
        {
            var blocker = runnability.Prerequisites.First(static p => !p.Satisfied);
            Console.Error.WriteLine($"error   : '{parsed.ProcessId}' is registered but not runnable here ({runnability.ExecutionModeLabel}).");
            Console.Error.WriteLine($"needs   : {blocker.Name} — {blocker.Detail}");
            Console.Error.WriteLine("          The devkit does not submit to a server; satisfy the prerequisite or run it in the integration stack.");
            return 1;
        }

        var result = await runner.RunAsync(parsed.ProcessId, inputs, capture).ConfigureAwait(false);
        var runnerLabel = gdalMode == GdalProcessExecutorMode.Container ? "container (honua-worker-etl image)" : "in-process";

        Console.WriteLine($"process : {result.ProcessId}");
        Console.WriteLine($"runner  : {runnerLabel}");
        Console.WriteLine($"status  : {result.Status}");
        Console.WriteLine($"elapsed : {result.Elapsed.TotalMilliseconds:F1} ms");

        if (!parsed.NoReceipt)
        {
            // The run receipt directory holds the same honua.gp.events/v1 evidence a remote
            // job leaves behind, so a local failure can be read the way a remote one is.
            var receiptPath = await WriteRunReceiptAsync(parsed.ReceiptDir ?? Path.Join(".honua", "runs"), result, runnerLabel).ConfigureAwait(false);
            Console.WriteLine($"receipt : {receiptPath}");
        }

        foreach (var command in result.Logs
            .Select(static log => log.Metadata is not null &&
                log.Metadata.TryGetValue("gdal.command", out var command)
                    ? command
                    : null)
            .OfType<string>())
        {
            // Default path prints the SANITIZED command (scratch redacted to <scratch>).
            Console.WriteLine($"command : {command}");
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

    /// <summary>
    /// Writes <c>&lt;dir&gt;/&lt;runId&gt;/receipt.json</c> and <c>events.jsonl</c>
    /// (<c>honua.gp.events/v1</c>, see docs/reference/gp-harness-events.v1.md) for a devkit
    /// run. Returns the receipt path.
    /// </summary>
    private static async Task<string> WriteRunReceiptAsync(string receiptRoot, LocalRunResult result, string runnerLabel)
    {
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Join(Path.GetFullPath(receiptRoot), runId);
        Directory.CreateDirectory(runDirectory);

        var eventsPath = Path.Join(runDirectory, "events.jsonl");
        await using (var writer = new StreamWriter(eventsPath, append: false))
        {
            HarnessEvents.WriteDevkitRun(writer, result, result.Phases);
        }

        var receiptPath = Path.Join(runDirectory, "receipt.json");
        await using (var stream = File.Create(receiptPath))
        await using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("schemaVersion", "1.0");
            json.WriteString("runId", runId);
            json.WriteString("processId", result.ProcessId);
            json.WriteString("executionMode", "devkit");
            json.WriteString("runner", runnerLabel);
            json.WriteString("contentClassification", "local-development-unsanitized");
            json.WriteString("status", result.Succeeded ? "succeeded" : "failed");
            json.WriteNumber("exitCode", result.Succeeded ? 0 : 1);
            json.WriteNumber("elapsedMs", (long)result.Elapsed.TotalMilliseconds);
            if (result.ErrorMessage is not null)
            {
                json.WriteString("error", result.ErrorMessage);
            }

            json.WriteStartArray("artifacts");
            foreach (var artifact in result.Artifacts)
            {
                json.WriteStringValue(Summarize(artifact));
            }

            json.WriteEndArray();
            json.WriteStartArray("warnings");
            foreach (var warning in result.Warnings)
            {
                json.WriteStringValue(warning);
            }

            json.WriteEndArray();
            json.WriteString("events", "events.jsonl");
            json.WriteString("eventsSchema", HarnessEvents.Schema);
            json.WriteEndObject();
        }

        return receiptPath;
    }

    private static bool IsEnvTruthy(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
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

        var configuration = GpContainerConfiguration.Create();
        var image = configuration["GdalContainer:Image"]!;
        var imageAvailable = await GdalContainerProbe.IsImageAvailableAsync(
            image, configuration["GdalContainer:DockerExecutable"]!).ConfigureAwait(false);

        if (forceContainer == true)
        {
            if (!imageAvailable)
            {
                throw new GpCliUsageException(
                    $"--real-worker requires the '{image}' worker image, which is not " +
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
        string? receiptDir = null;
        var noReceipt = false;
        var glassBoxRequested = false;
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
                case "--receipt-dir":
                    receiptDir = NextValue(args, ref i, "--receipt-dir");
                    break;
                case "--no-receipt":
                    noReceipt = true;
                    break;
                case "--glass-box" or "--debug" or "-d":
                    glassBoxRequested = true;
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

        return new ProcessArgs(processId, inputPath, outPath, glassBoxRequested, forceContainer, inputs, receiptDir, noReceipt);
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
        string? project = null;
        string? harnessRoot = null;
        var update = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" or "-r":
                    root = NextValue(args, ref i, "--root");
                    break;
                case "--project":
                    project = NextValue(args, ref i, "--project");
                    break;
                case "--harness-root":
                    harnessRoot = NextValue(args, ref i, "--harness-root");
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

        // A custom-code project (honua-gp.yaml) is tested through its own local harness, not
        // the in-checkout executor fixtures.
        var projectDirectory = project ?? (File.Exists(Path.Join(Directory.GetCurrentDirectory(), GpProjectManifest.FileName)) ? Directory.GetCurrentDirectory() : null);
        if (projectDirectory is not null)
        {
            if (onlyId is not null || root is not null)
            {
                throw new GpCliUsageException("A custom-code project test takes --project [--update] [--harness-root <checkout>], not a fixture id or --root.");
            }

            return await RunProjectTestAsync(projectDirectory, harnessRoot, update).ConfigureAwait(false);
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
        var executors = ResolveExecutors(provider);
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

    private static async Task<int> RunProjectTestAsync(string projectDirectory, string? explicitHarnessRoot, bool update)
    {
        var manifestPath = Path.Join(Path.GetFullPath(projectDirectory), GpProjectManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new GpCliUsageException($"{projectDirectory} holds no {GpProjectManifest.FileName}; run `honua-gp-devkit init` first or pass --project <dir>.");
        }

        GpProjectManifest manifest;
        try
        {
            manifest = GpProjectManifest.Load(manifestPath);
        }
        catch (InvalidDataException ex)
        {
            throw new GpCliUsageException(ex.Message);
        }

        var projectRoot = Path.GetFullPath(projectDirectory);
        var harnessRoot = GpCustomProjectScaffolder.ResolveHarnessRoot(
            explicitHarnessRoot ?? manifest.HarnessRoot,
            projectRoot,
            manifest.ToolchainVersion);
        var verdict = GpProjectDiscovery.Assess(manifest, projectRoot, new GpEnvironmentProbe(), harnessRoot, allowInstalledRestore: true);
        if (!verdict.RunnableHere)
        {
            // Fail before restoring a toolchain or starting a harness. The named prerequisite
            // is the whole answer; this command never submits the project to a server.
            Console.Error.WriteLine($"error   : {GpProjectDiscovery.BlockedError(manifest, verdict)}");
            Console.WriteLine("summary : FAILED");
            return 1;
        }

        if (harnessRoot is null)
        {
            try
            {
                harnessRoot = GpBundledToolchain.Restore(projectDirectory, manifest.ToolchainVersion);
            }
            catch (InvalidDataException ex)
            {
                throw new GpCliUsageException(ex.Message);
            }
        }

        return await FinishProjectTestAsync(manifest, projectRoot, harnessRoot, update).ConfigureAwait(false);
    }

    private static (GpProjectManifest Manifest, GpRunnability Verdict) DiscoverProject(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(root))
        {
            throw new GpCliUsageException($"Project directory '{projectDirectory}' does not exist.");
        }

        var manifestPath = Path.Join(root, GpProjectManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new GpCliUsageException($"{root} holds no {GpProjectManifest.FileName}; run `honua-gp-devkit init` first or pass --project <dir>.");
        }

        GpProjectManifest manifest;
        try
        {
            manifest = GpProjectManifest.Load(manifestPath);
        }
        catch (InvalidDataException ex)
        {
            throw new GpCliUsageException(ex.Message);
        }

        var harness = GpCustomProjectScaffolder.ResolveHarnessRoot(manifest.HarnessRoot, root, manifest.ToolchainVersion);
        return (manifest, GpProjectDiscovery.Assess(manifest, root, new GpEnvironmentProbe(), harness, allowInstalledRestore: true));
    }

    private static async Task<int> FinishProjectTestAsync(GpProjectManifest manifest, string projectRoot, string harnessRoot, bool update)
    {
        Console.WriteLine($"project : {manifest.Name} ({(manifest.Language == GpProjectLanguage.Python ? "python" : "dotnet")}, {manifest.Entrypoint})");
        Console.WriteLine($"harness : {harnessRoot}");
        Console.WriteLine($"mode    : {(update ? "UPDATE (rewriting expected artifacts)" : "assert")}");
        var result = await GpCustomProjectTester.RunAsync(projectRoot, harnessRoot, update).ConfigureAwait(false);
        foreach (var line in result.Lines)
        {
            Console.WriteLine(line);
        }

        if (result.ReceiptPath is not null)
        {
            Console.WriteLine($"receipt : {result.ReceiptPath}");
        }

        if (result.Error is not null)
        {
            Console.Error.WriteLine($"error   : {result.Error}");
        }

        Console.WriteLine(result.Passed ? "summary : passed" : "summary : FAILED");
        return result.Passed ? 0 : 1;
    }

    private static int RunInit(string[] args)
    {
        string? language = null;
        string? name = null;
        string? output = null;
        string? harnessRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--language" or "-l":
                    language = NextValue(args, ref i, "--language");
                    break;
                case "--name" or "-n":
                    name = NextValue(args, ref i, "--name");
                    break;
                case "--output" or "-o":
                    output = NextValue(args, ref i, "--output");
                    break;
                case "--harness-root":
                    harnessRoot = NextValue(args, ref i, "--harness-root");
                    break;
                default:
                    throw new GpCliUsageException($"Unknown option '{args[i]}'. Usage: honua-gp-devkit init --language python|dotnet --name <process> [--output <dir>] [--harness-root <checkout>]");
            }
        }

        var projectLanguage = (language ?? "").ToLowerInvariant() switch
        {
            "python" or "py" => GpProjectLanguage.Python,
            "dotnet" or "csharp" or "cs" => GpProjectLanguage.Dotnet,
            _ => throw new GpCliUsageException("--language must be python or dotnet."),
        };
        if (!GpCustomProjectScaffolder.TryValidateName(name, out var error))
        {
            throw new GpCliUsageException($"--name: {error}");
        }

        var projectDirectory = Path.GetFullPath(Path.Join(output ?? Directory.GetCurrentDirectory(), name!));
        var resolvedHarness = GpCustomProjectScaffolder.ResolveHarnessRoot(harnessRoot, projectDirectory);
        var plan = GpCustomProjectScaffolder.Plan(projectLanguage, name!, resolvedHarness);
        try
        {
            GpCustomProjectScaffolder.Apply(projectDirectory, plan);
        }
        catch (IOException ex)
        {
            throw new GpCliUsageException(ex.Message);
        }

        Console.WriteLine($"created : {projectDirectory}");
        Console.WriteLine($"template: {GpCustomProjectScaffolder.TemplateFor(projectLanguage)} from {GpCustomProjectScaffolder.TemplateSource}@{GpCustomProjectScaffolder.TemplateCommit[..10]} ({plan.Count} files)");
        Console.WriteLine($"harness : {resolvedHarness ?? "bundled .honua/toolchain (installed devkit version)"}");
        Console.WriteLine("next    : edit process/, then `honua-gp-devkit test --project " + projectDirectory + "`");
        return 0;
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
        var executors = ResolveExecutors(provider);
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
            // file.RelativePath is generated by GpScaffolder from a processId already validated
            // by TryValidateProcessId (dotted-segment identifier), so it is always a relative,
            // traversal-free path — never rooted or attacker-controlled.
            foreach (var file in plan.Files)
            {
                var target = Path.Join(outputDir, file.RelativePath);
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
        // file.RelativePath is generated by GpScaffolder from a processId already validated
        // by TryValidateProcessId (dotted-segment identifier), so it is always a relative,
        // traversal-free path — never rooted or attacker-controlled.
        foreach (var file in plan.Files)
        {
            var target = Path.Join(repoRoot, file.RelativePath);
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
        var executors = ResolveExecutors(provider);
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

        // Publishing a workflow package can involve a non-trivial upload; use a generous
        // but bounded timeout rather than none (the CLI would otherwise hang indefinitely
        // on an unresponsive/unreachable --server).
        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) };
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
        // repoRoot is an absolute directory path from ResolveRepoRoot(), and both branches
        // are compile-time literal repo-relative paths, so Path.Combine cannot silently drop
        // either segment here.
        var registrationFile = Path.Join(
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
        // runtime profile). repoRoot is absolute and the second segment is a compile-time
        // literal, so Path.Combine cannot silently drop either argument here.
        var catalogFile = Path.Join(
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

        var capabilityFile = Path.Join(
            repoRoot,
            "src/Honua.Geoprocessing/Features/Geoprocessing/ProcessExecutionCapabilityCatalog.cs");
        if (File.Exists(capabilityFile))
        {
            var source = await File.ReadAllTextAsync(capabilityFile).ConfigureAwait(false);
            if (GpScaffoldInjector.TryInsertJobClassification(source, processId, out var updated, out var error))
            {
                await File.WriteAllTextAsync(capabilityFile, updated).ConfigureAwait(false);
                Console.WriteLine($"  classified {processId} as a job capability");
            }
            else
            {
                Console.WriteLine($"  NOTE: could not auto-classify ({error}). Add the process id to JobProcessIds by hand.");
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
        // dir.FullName is always an absolute directory path from DirectoryInfo, and "Honua.sln"
        // is a compile-time literal, so Path.Combine cannot silently drop either segment here.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Join(dir.FullName, "Honua.sln")))
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

        // dir.FullName is always an absolute directory path from DirectoryInfo, and
        // "samples"/"gp" are compile-time literals, so Path.Combine cannot silently drop
        // any of these segments here.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "samples", "gp");
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
        bool? ForceContainer,
        Dictionary<string, string> Params,
        string? ReceiptDir = null,
        bool NoReceipt = false);

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

        return definition.Parameters
            .FirstOrDefault(parameter => parameter.Required
                && parameter.ValueType is ProcessParameterValueType.Wkb
                    or ProcessParameterValueType.WkbArray
                    or ProcessParameterValueType.Text)
            ?.Name;
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
        Console.WriteLine("  Installed tool: honua-gp-devkit <command> (equivalent to the honua gp command family below)");
        Console.WriteLine("  honua gp doctor [--managed-only] [--json]  (also reports docker, kind, kubectl, and helm)");
        Console.WriteLine("  honua gp list [--json] [--probe-container] [--project <dir>]");
        Console.WriteLine("  honua gp describe <processId> [--json] [--probe-container]");
        Console.WriteLine("  honua gp describe --project <dir> [--json]");
        Console.WriteLine("  honua gp plan <processId> [--input <file>] [--param k=v ...]");
        Console.WriteLine("  honua gp run  <processId> [--input <file>] [--param k=v ...] [--out <file>] [--glass-box] [--real-worker]");
        Console.WriteLine("  honua gp test [<fixtureId>] [--root <dir>] [--update]");
        Console.WriteLine("  honua gp test --project <dir> [--update] [--harness-root <checkout>]   (custom-code project)");
        Console.WriteLine("  honua gp init --language python|dotnet --name <process> [--output <dir>] [--harness-root <checkout>]");
        Console.WriteLine("  honua gp new <id> [--kind geometry|gdal] [--output <dir>]");
        Console.WriteLine("  honua gp publish <id> [--server <url>] [--api-key <key>] [--message <m>] [--dry-run] [--publish]");
        Console.WriteLine("  honua gp release [--descriptor honua-gp.yaml] --output <dir> [--source-repository o/n] [--source-sha <sha>] [--runtime-image <ref@sha256:..>] [--json]");
        Console.WriteLine("  honua gp release verify <lock.json> [--artifacts <dir>] [--json]");
        Console.WriteLine("  honua gp reproduce <bundle.json> [--lock <lock.json>] [--checkout <dir>] [--process <id>]");
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
        Console.WriteLine("  HONUA_GP_WORKER_IMAGE / HONUA_GP_CONTAINER_USER / HONUA_GP_CONTAINER_RUNTIME configure native containers.");
        Console.WriteLine("  --real-worker       Run native gdal.* steps inside the real honua-worker-etl");
        Console.WriteLine("  (--container)       image (docker run) instead of the host GDAL CLIs, so the run");
        Console.WriteLine("                      crosses the same image/CRS/arg boundary a native submit does.");
        Console.WriteLine("                      Default: auto (container when the image is present, else in-process).");
        Console.WriteLine("  --in-process        Force the fast host-CLI path even when the worker image is present.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  doctor  Report the managed probe, native image, and integration profile. Also reports");
        Console.WriteLine("          docker, kind, kubectl, and helm as found or missing, with one remediation line each.");
        Console.WriteLine("          Missing tools do not change the exit code.");
        Console.WriteLine("  describe  Show a process's typed parameters, outputs, inputs JSON Schema, and an example (--json for the machine view).");
        Console.WriteLine("            --project <dir> reports that custom project's runtime, dependency lock, and local harness (runnableHere). It never submits to a server.");
        Console.WriteLine("  plan  Dry-run: validate params + DAG and estimate output size/cost WITHOUT executing.");
        Console.WriteLine("  run   Execute the process and emit its artifact(s).");
        Console.WriteLine("  test  Run golden-file fixtures under samples/gp (or --root) and assert outputs;");
        Console.WriteLine("        with --project (or a honua-gp.yaml in the cwd) run a custom-code project through its local harness");
        Console.WriteLine("        and compare the artifacts with expected/ (first difference + receipt path on failure).");
        Console.WriteLine("  init  Scaffold a custom-code project (Python or .NET) from the honua-samples templates:");
        Console.WriteLine("        working process, input schema, fixtures, expected artifacts, dependency lock, VS Code debugging.");
        Console.WriteLine("  --probe-container   list/describe: also check the GDAL worker image (shells out to the container runtime).");
        Console.WriteLine("  publish  Push an authored workflow to the WorkflowPackage store (GitOps approval-gated promotion).");
        Console.WriteLine("  reproduce  Run a diagnostic bundle locally from its exported inputs.");
        Console.WriteLine("        Does not replay production writes, reuse production paths, or move HEAD.");
        Console.WriteLine("        Exits 0 only when the local outcome matches the recorded one, including a recorded failure.");
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
        Console.WriteLine("  honua gp reproduce job-diagnostic-bundle.json --lock process-release-lock.json");
    }
}

/// <summary>
/// Signals a CLI usage error that should print the message plus usage and exit
/// non-zero, distinct from an executor-reported run failure.
/// </summary>
internal sealed class GpCliUsageException(string message) : Exception(message);
