// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the <c>gdal.gdalwarp</c>
/// process: raster reprojection backed by the GDAL <c>gdalwarp</c> CLI.
///
/// This is the heavyweight counterpart to the lean managed
/// <c>geometry.project</c> executor, which deliberately rejects datum-shift
/// transforms that require PROJ. <c>gdalwarp</c> performs full PROJ-backed raster
/// reprojection. It runs only inside the GDAL worker image (it declares
/// <see cref="AcceptedRuntimeProfiles"/> = <c>{ "native" }</c>). It reads a
/// base64 GeoTIFF source and a target CRS from the durable spec, reprojects in an
/// isolated scratch workspace, and publishes the reprojected GeoTIFF as a
/// canonical data-URI artifact.
/// </summary>
public sealed partial class GdalRasterReprojectJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterReprojectJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "gdal.gdalwarp";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string>? AcceptedRuntimeProfiles { get; } =
        new HashSet<string>(StringComparer.Ordinal) { GdalWorkerParameterKeys.NativeRuntimeProfile };

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
        await context.ReportProgressAsync(5, "Parsing reprojection inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "targetSrs", out var targetSrs))
        {
            return JobExecutionResult.Failed("Invalid reprojection inputs: missing required input 'targetSrs'.");
        }

        if (!IsValidSrsToken(targetSrs))
        {
            return JobExecutionResult.Failed(
                $"Invalid reprojection inputs: 'targetSrs' value '{targetSrs}' is not an accepted CRS token " +
                "(expected an EPSG code like 'EPSG:3857' or a positive integer).");
        }

        GdalJobInputReader.TryGetInput(parameters, "sourceSrs", out var sourceSrs);

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", out var sourceBytes, out var inputError))
        {
            Log.InvalidInputs(logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid reprojection inputs: {inputError}");
        }

        if (sourceBytes.Length > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed(
                $"Source raster {sourceBytes.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalwarp reprojection", cancellationToken).ConfigureAwait(false);

            var args = new List<string> { "-overwrite", "-of", "GTiff" };
            if (!string.IsNullOrWhiteSpace(sourceSrs) && IsValidSrsToken(sourceSrs))
            {
                args.Add("-s_srs");
                args.Add(NormalizeSrs(sourceSrs));
            }

            args.Add("-t_srs");
            args.Add(NormalizeSrs(targetSrs));
            args.Add(inputPath);
            args.Add(outputPath);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdalwarp", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdalwarp timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, Truncate(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalwarp exited with code {result.ExitCode}: {Truncate(result.StandardError)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed(
                    "gdalwarp reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding reprojected raster artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdalwarp produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Reprojected raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = BuildDataUri(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Reprojection completed", cancellationToken).ConfigureAwait(false);

            Log.ReprojectionCompleted(logger, job.OperationId, targetSrs, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            TryCleanup(workspace);
        }
    }

    /// <summary>
    /// Validates a CRS token to a conservative allow-shape before passing it to
    /// the CLI. Accepts a bare positive integer (EPSG code) or an
    /// <c>AUTHORITY:CODE</c> token. This blocks shell-influencing values and
    /// arbitrary PROJ strings while covering the common reprojection cases.
    /// </summary>
    private static bool IsValidSrsToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        if (int.TryParse(token, out var epsg) && epsg > 0)
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

    private static string NormalizeSrs(string value)
    {
        var token = value.Trim();
        return int.TryParse(token, out _) ? $"EPSG:{token}" : token;
    }

    private static string BuildDataUri(string contentType, byte[] payload)
    {
        var sb = new StringBuilder(payload.Length * 2 + 64);
        sb.Append("data:");
        sb.Append(contentType);
        sb.Append(";base64,");
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private void TryCleanup(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
        catch (IOException ex)
        {
            Log.CleanupFailed(logger, workspace, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.CleanupFailed(logger, workspace, ex.Message);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9220, LogLevel.Warning,
            "GDAL raster reproject executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9221, LogLevel.Warning,
            "GDAL raster reproject executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9222, LogLevel.Error,
            "GDAL raster reproject executor failed job {OperationId}: gdalwarp exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9223, LogLevel.Error,
            "GDAL raster reproject executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9224, LogLevel.Warning,
            "GDAL raster reproject executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9225, LogLevel.Information,
            "GDAL raster reproject executor completed job {OperationId}: targetSrs={TargetSrs}, bytes={Bytes}")]
        public static partial void ReprojectionCompleted(ILogger logger, string operationId, string targetSrs, long bytes);

        [LoggerMessage(9226, LogLevel.Warning,
            "GDAL worker scratch cleanup failed for {Workspace}: {Reason}")]
        public static partial void CleanupFailed(ILogger logger, string workspace, string reason);
    }
}
