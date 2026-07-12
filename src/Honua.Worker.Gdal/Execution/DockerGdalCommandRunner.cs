// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Opt-in container-exec <see cref="IGdalCommandRunner"/> (issue #2180) that runs
/// each GDAL/OGR tool inside the real <c>honua-worker-etl</c> image via
/// <c>docker run</c>, instead of shelling out to the host's in-process GDAL CLIs
/// (<see cref="ProcessGdalCommandRunner"/>).
///
/// <para>
/// <b>Why this exists.</b> The GP Devkit local runner runs the real native executor
/// in-process for a sub-second loop, but the in-process path never crosses the
/// image / driver-set / CRS-data / arg-handling boundary a production native submit
/// crosses (the job is packaged into the GDAL worker container and dispatched by
/// AWS Batch). A <c>gdal.hillshade</c> that passes <c>gp run</c> against a host's
/// GDAL could still fail at that boundary. Running the tool inside the SAME image
/// Batch packages closes that fidelity cliff while keeping <c>gp plan</c>/<c>gp run</c>
/// a true dry-run of the native submit path.
/// </para>
///
/// <para>
/// <b>How it stays correct-by-construction.</b> Every native executor does ALL of its
/// file I/O inside a per-job scratch workspace under <c>GdalWorkerOptions.ScratchRoot</c>
/// and passes that workspace as the tool's working directory, with absolute
/// workspace paths in the argument vector (e.g. <c>gdaldem hillshade …
/// /scratch/&lt;op&gt;/input.tif /scratch/&lt;op&gt;/output.tif</c>). This runner bind-mounts
/// the host workspace into the container at the <em>identical absolute path</em>
/// (<c>-v &lt;workspace&gt;:&lt;workspace&gt;</c>, <c>-w &lt;workspace&gt;</c>), so those absolute
/// paths resolve to the same files inside the container; the tool reads the inputs
/// the executor materialized and writes its outputs back onto the host workspace
/// where the executor then reads them. The executor code path is therefore byte-for-
/// byte unchanged — only the <see cref="IGdalCommandRunner"/> implementation differs.
/// </para>
/// </summary>
internal sealed partial class DockerGdalCommandRunner(
    IDockerCommandInvoker invoker,
    IOptions<GdalContainerExecutionOptions> options,
    IOptions<GdalHardeningOptions> hardening,
    ILogger<DockerGdalCommandRunner> logger) : IGdalCommandRunner
{
    /// <inheritdoc />
    public Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var opts = options.Value;
        var hardeningEnv = GdalRuntimeHardening.BuildEnvironment(
            hardening.Value,
            GdalRuntimeHardening.ArgumentsReferenceVsi(arguments));
        var dockerArgs = BuildDockerRunArguments(opts, tool, arguments, workingDirectory, hardeningEnv);

        Log.DispatchingToContainer(logger, tool, opts.Image, workingDirectory);
        return invoker.RunAsync(opts.DockerExecutable, dockerArgs, cancellationToken);
    }

    /// <summary>
    /// Builds the full <c>docker run …</c> argument vector for one GDAL tool
    /// invocation. Pure and deterministic so it can be asserted offline (the docker
    /// invocation is the load-bearing, correct-by-construction part of this runner).
    ///
    /// <para>The layout is:</para>
    /// <code>
    /// run --rm --network &lt;net&gt; --user &lt;uid:gid&gt;
    ///     -v &lt;workspace&gt;:&lt;workspace&gt; -w &lt;workspace&gt;
    ///     --entrypoint &lt;tool&gt; &lt;image&gt; &lt;...toolArgs&gt;
    /// </code>
    /// <list type="bullet">
    /// <item><c>--rm</c>: the container is single-shot per tool invocation; nothing
    /// persists outside the bind-mounted workspace.</item>
    /// <item><c>--network</c>: hermetic by default (<c>none</c>) — every op runs purely
    /// on the bind-mounted scratch files.</item>
    /// <item><c>--user</c>: pins uid:gid so files the tool writes into the host-owned
    /// workspace are owned consistently and stay readable by the host runner.</item>
    /// <item><c>-v &lt;workspace&gt;:&lt;workspace&gt;</c> + <c>-w &lt;workspace&gt;</c>: the
    /// identical-path bind mount that makes the executor's absolute workspace paths
    /// resolve inside the container.</item>
    /// <item><c>--entrypoint &lt;tool&gt;</c>: overrides the image's
    /// <c>dotnet Honua.Worker.Gdal.dll</c> entrypoint so we invoke the raw GDAL CLI
    /// (<c>gdalwarp</c>/<c>ogr2ogr</c>/<c>gdaldem</c>/…) the image ships on PATH —
    /// the same binaries the in-container worker shells out to.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> BuildDockerRunArguments(
        GdalContainerExecutionOptions options,
        string tool,
        IReadOnlyList<string> toolArguments,
        string workspace,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(toolArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var envArgs = environment is null
            ? []
            : GdalRuntimeHardening.ToDockerEnvArguments(environment);

        var args = new List<string>(toolArguments.Count + envArgs.Count + 14)
        {
            "run",
            "--rm",
        };

        if (!string.IsNullOrWhiteSpace(options.Network))
        {
            args.Add("--network");
            args.Add(options.Network);
        }

        if (!string.IsNullOrWhiteSpace(options.User))
        {
            args.Add("--user");
            args.Add(options.User);
        }

        // Propagate the GDAL hardening environment into the container so the
        // driver-skip / remote-VSI-disable policy applies inside the image too (#2765).
        args.AddRange(envArgs);

        // Identical-path bind mount: the executor's absolute workspace paths must
        // resolve to the same files inside the container.
        args.Add("-v");
        args.Add($"{workspace}:{workspace}");
        args.Add("-w");
        args.Add(workspace);

        // Override the image entrypoint (dotnet Honua.Worker.Gdal.dll) so the raw
        // GDAL tool on the image PATH is what runs.
        args.Add("--entrypoint");
        args.Add(tool);

        args.Add(options.Image);
        args.AddRange(toolArguments);

        return args;
    }

    private static partial class Log
    {
        [LoggerMessage(9273, LogLevel.Information,
            "Dispatching GDAL tool {Tool} to container image {Image} (workspace {Workspace})")]
        public static partial void DispatchingToContainer(ILogger logger, string tool, string image, string workspace);
    }
}
