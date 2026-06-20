// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the <c>pcloud.translate</c>
/// process: decompresses a LAZ or Cloud-Optimized Point Cloud (COPC) source and,
/// optionally, reprojects its horizontal datum, emitting an <em>uncompressed</em>
/// LAS artifact (#1854).
///
/// This is the point-cloud counterpart to <see cref="GdalRasterReprojectJobExecutor"/>:
/// it shells out to the PDAL <c>pdal translate</c> CLI (built on laz-perf for LAZ
/// decompression, a native COPC reader, and PROJ for reprojection) rather than
/// bundling any native bindings, so every managed assembly stays native-dep-free.
/// It runs only inside the heavyweight worker image (it overrides
/// <see cref="AcceptedRuntimeProfiles"/> to <c>{ "native" }</c>).
///
/// <para>
/// The deliberate contract is: the worker does ONLY the work that genuinely needs
/// native tooling — decompression and (when requested) datum reprojection — and
/// returns plain uncompressed LAS. The server-side managed pipeline
/// (<c>LasPointCloudReader</c> + <c>PointCloudTilesetBuilder</c> + <c>PntsTileWriter</c>,
/// #1840) then tiles that LAS into 3D Tiles exactly as for a natively-uploaded LAS.
/// No 3D-Tiles generation lives in the worker.
/// </para>
///
/// <para>
/// Reprojection target is fixed to EPSG:4979 (WGS-84 geographic 3D) — the CRS the
/// managed tiler expects and the dataset registers under — so a projected source
/// LAZ/COPC lands as geographic lon/lat/ellipsoidal-height ready for the
/// geographic-pass-through tiler. The horizontal reprojection is requested only
/// when a non-geographic <c>sourceSrs</c> is supplied; a source already in a
/// geographic CRS is decompressed verbatim.
/// </para>
/// </summary>
internal sealed partial class PdalPointCloudConvertJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<PdalPointCloudConvertJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "pcloud.translate";

    /// <summary>
    /// The fixed reprojection target. Point clouds are tiled onto the WGS-84
    /// ellipsoid with ellipsoidal height in meters, so a projected source is
    /// reprojected to EPSG:4979 (geographic 3D) before tiling.
    /// </summary>
    public const string TargetSrs = "EPSG:4979";

    private const string UncompressedLasContentType = "application/vnd.las";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GdalJobInputReader.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing point-cloud conversion inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        // A source CRS is optional: when supplied AND not already geographic, the
        // executor reprojects to EPSG:4979; otherwise it decompresses verbatim. An
        // explicitly geographic source (EPSG:4326/4979/CRS84) is a no-op reproject
        // so we skip the filter to keep the output coordinates byte-identical to a
        // verbatim decompress.
        GdalJobInputReader.TryGetInput(parameters, "sourceSrs", out var sourceSrs);
        var hasSourceSrs = !string.IsNullOrWhiteSpace(sourceSrs);
        if (hasSourceSrs && !IsValidSrsToken(sourceSrs))
        {
            return JobExecutionResult.Failed(
                $"Invalid point-cloud conversion inputs: 'sourceSrs' value '{sourceSrs}' is not an accepted CRS token " +
                "(expected an EPSG code like 'EPSG:32610' or a positive integer).");
        }

        var reproject = hasSourceSrs && !IsGeographicSrs(sourceSrs);

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var inputError))
        {
            Log.InvalidInputs(logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid point-cloud conversion inputs: {inputError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // PDAL infers the reader driver from the input extension. Naming the
            // input ".laz" selects the LAS/LAZ reader, which transparently handles
            // both plain LAZ and COPC (a COPC file is a valid LAZ whose reader the
            // driver auto-detects). The output ".las" with compression off forces
            // an uncompressed LAS the managed reader can parse directly.
            var inputPath = Path.Combine(workspace, "input.laz");
            var outputPath = Path.Combine(workspace, "output.las");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(
                40,
                reproject ? "Running pdal translate (decompress + reproject)" : "Running pdal translate (decompress)",
                cancellationToken).ConfigureAwait(false);

            var args = BuildTranslateArguments(inputPath, outputPath, reproject ? sourceSrs : null);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("pdal", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"pdal translate timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"pdal translate exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed(
                    "pdal translate reported success but produced no output point cloud.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding decompressed LAS artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("pdal translate produced an empty output point cloud.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Decompressed LAS size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(UncompressedLasContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Point-cloud conversion completed", cancellationToken).ConfigureAwait(false);

            Log.ConversionCompleted(logger, job.OperationId, reproject, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Builds the <c>pdal translate</c> argument vector: read
    /// <paramref name="inputPath"/>, write an uncompressed LAS at
    /// <paramref name="outputPath"/>, and — when <paramref name="sourceSrs"/> is
    /// supplied — apply the reprojection filter from that source CRS to
    /// <see cref="TargetSrs"/>. Each token is passed as a discrete argument (no
    /// shell), so the validated CRS token cannot influence argument parsing.
    /// </summary>
    internal static List<string> BuildTranslateArguments(string inputPath, string outputPath, string? sourceSrs)
    {
        var args = new List<string>
        {
            "translate",
            inputPath,
            outputPath,
        };

        if (!string.IsNullOrWhiteSpace(sourceSrs))
        {
            // Insert the reprojection stage and pin its in/out CRS. PDAL stage
            // options use the `--filters.<name>.<opt>=value` form.
            args.Add("reprojection");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--filters.reprojection.in_srs={NormalizeSrs(sourceSrs)}"));
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--filters.reprojection.out_srs={TargetSrs}"));
        }

        // Force an uncompressed LAS writer regardless of the output extension so
        // the managed LasPointCloudReader can parse the artifact directly.
        args.Add("--writers.las.compression=false");

        return args;
    }

    /// <summary>
    /// Validates a CRS token to a conservative allow-shape before passing it to
    /// the CLI: a bare positive integer (EPSG code) or an <c>AUTHORITY:CODE</c>
    /// token. This blocks shell-influencing values and arbitrary PROJ strings.
    /// Mirrors <see cref="GdalRasterReprojectJobExecutor"/>'s token guard.
    /// </summary>
    internal static bool IsValidSrsToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epsg) && epsg > 0)
        {
            return true;
        }

        var parts = token.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var authority = parts[0];
        var code = parts[1];
        if (authority.Length is 0 or > 16 || !authority.All(char.IsLetterOrDigit))
        {
            return false;
        }

        return code.Length is > 0 and <= 16 && code.All(char.IsLetterOrDigit);
    }

    /// <summary>
    /// Returns whether a (pre-validated) CRS token names a geographic WGS-84 CRS
    /// the tiler already accepts as a pass-through. Reprojection is skipped for
    /// these so a geographic source is decompressed verbatim with no datum filter.
    /// </summary>
    internal static bool IsGeographicSrs(string value)
    {
        var token = NormalizeSrs(value).Trim();
        return token is "EPSG:4326" or "EPSG:4979" or "OGC:CRS84" or "EPSG:4269";
    }

    private static string NormalizeSrs(string value)
    {
        var token = value.Trim();
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? $"EPSG:{token}"
            : token.ToUpperInvariant();
    }

    private static partial class Log
    {
        [LoggerMessage(9240, LogLevel.Warning,
            "PDAL point-cloud convert executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9241, LogLevel.Warning,
            "PDAL point-cloud convert executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9242, LogLevel.Error,
            "PDAL point-cloud convert executor failed job {OperationId}: pdal translate exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9243, LogLevel.Error,
            "PDAL point-cloud convert executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9244, LogLevel.Warning,
            "PDAL point-cloud convert executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9245, LogLevel.Information,
            "PDAL point-cloud convert executor completed job {OperationId}: reprojected={Reprojected}, bytes={Bytes}")]
        public static partial void ConversionCompleted(ILogger logger, string operationId, bool reprojected, long bytes);
    }
}
