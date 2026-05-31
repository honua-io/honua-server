// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the <c>raster.clip</c> process:
/// raster clipping to a polygonal boundary geometry, executed via the GDAL
/// <c>gdalwarp -cutline</c> CLI. Reads a base64 GeoTIFF source and a base64
/// WKB boundary, writes the boundary to a GeoJSON cutline file (gdalwarp's
/// <c>-cutline</c> requires an OGR-readable file; it cannot consume inline WKB),
/// runs the clip in an isolated scratch workspace, and publishes the clipped
/// GeoTIFF as a canonical data-URI artifact. Runs only inside the GDAL worker
/// image — <see cref="AcceptedRuntimeProfiles"/> is <c>{ "native" }</c>.
/// </summary>
public sealed partial class GdalRasterClipJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterClipJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.clip";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

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
        await context.ReportProgressAsync(5, "Parsing clip inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid clip inputs: {sourceError}");
        }

        if (sourceBytes.Length > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed(
                $"Source raster {sourceBytes.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "boundary", out var boundaryWkb, out var boundaryError))
        {
            Log.InvalidInputs(logger, job.OperationId, boundaryError);
            return JobExecutionResult.Failed($"Invalid clip inputs: {boundaryError}");
        }

        Geometry boundary;
        try
        {
            boundary = new WKBReader().Read(boundaryWkb);
        }
        catch (ParseException ex)
        {
            return JobExecutionResult.Failed($"Invalid clip inputs: boundary WKB could not be parsed: {ex.Message}");
        }

        if (boundary == null || boundary.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid clip inputs: boundary geometry is empty.");
        }

        if (!IsAreaGeometry(boundary))
        {
            return JobExecutionResult.Failed(
                $"Invalid clip inputs: boundary must be a Polygon or MultiPolygon; got '{boundary.GeometryType}'.");
        }

        if (GdalJobInputReader.TryGetInput(parameters, "boundarySrid", out var boundarySridRaw)
            && !string.IsNullOrWhiteSpace(boundarySridRaw))
        {
            if (!int.TryParse(boundarySridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) || srid <= 0)
            {
                return JobExecutionResult.Failed(
                    $"Invalid clip inputs: 'boundarySrid' value '{boundarySridRaw}' is not a positive integer.");
            }
            boundary.SRID = srid;
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var cutlinePath = Path.Combine(workspace, "cutline.geojson");
            var outputPath = Path.Combine(workspace, "output.tif");

            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            var cutlineGeoJson = WriteCutlineGeoJson(boundary);
            await File.WriteAllTextAsync(cutlinePath, cutlineGeoJson, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalwarp clip", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-overwrite",
                "-of", "GTiff",
                "-cutline", cutlinePath,
                "-crop_to_cutline",
                inputPath,
                outputPath,
            };

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
                return JobExecutionResult.Failed("gdalwarp reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding clipped raster artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdalwarp produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Clipped raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Clip completed", cancellationToken).ConfigureAwait(false);

            Log.ClipCompleted(logger, job.OperationId, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static bool IsAreaGeometry(Geometry geometry)
        => geometry is Polygon or MultiPolygon;

    /// <summary>
    /// Encodes the boundary as a single-feature GeoJSON FeatureCollection that
    /// gdalwarp's <c>-cutline</c> option will accept. The CRS field is emitted
    /// only when the boundary carries an explicit SRID so we never invent CRS
    /// metadata that gdalwarp would silently honour.
    /// </summary>
    internal static string WriteCutlineGeoJson(Geometry boundary)
    {
        var geometryJson = new GeoJsonWriter().Write(boundary);
        var sb = new StringBuilder(geometryJson.Length + 256);
        sb.Append("{\"type\":\"FeatureCollection\"");
        if (boundary.SRID > 0)
        {
            sb.Append(",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"EPSG:");
            sb.Append(boundary.SRID.ToString(CultureInfo.InvariantCulture));
            sb.Append("\"}}");
        }
        sb.Append(",\"features\":[{\"type\":\"Feature\",\"properties\":{},\"geometry\":");
        sb.Append(geometryJson);
        sb.Append("}]}");
        return sb.ToString();
    }

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static partial class Log
    {
        [LoggerMessage(9250, LogLevel.Warning,
            "GDAL raster clip executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9251, LogLevel.Warning,
            "GDAL raster clip executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9252, LogLevel.Error,
            "GDAL raster clip executor failed job {OperationId}: gdalwarp exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9253, LogLevel.Error,
            "GDAL raster clip executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9254, LogLevel.Warning,
            "GDAL raster clip executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9255, LogLevel.Information,
            "GDAL raster clip executor completed job {OperationId}: bytes={Bytes}")]
        public static partial void ClipCompleted(ILogger logger, string operationId, long bytes);
    }
}
