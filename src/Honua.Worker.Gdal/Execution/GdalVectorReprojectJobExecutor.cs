// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class GdalVectorReprojectJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalVectorReprojectJobExecutor> logger) : IProcessExecutor
{
    public const string HandledProcessId = "transform.reproject";

    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    private const string GeoJsonContentType = "application/geo+json";

    private const string GeoJsonDataUriPrefix = "data:application/geo+json;base64,";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

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

        if (!TryReadSrid(parameters, "fromSrid", out var fromSrid, out var fromError))
        {
            return JobExecutionResult.Failed($"Invalid reprojection inputs: {fromError}");
        }

        if (!TryReadSrid(parameters, "toSrid", out var toSrid, out var toError))
        {
            return JobExecutionResult.Failed($"Invalid reprojection inputs: {toError}");
        }

        if (!GdalJobInputReader.TryGetInput(parameters, "input", out var inputUri)
            || string.IsNullOrWhiteSpace(inputUri))
        {
            return JobExecutionResult.Failed("Invalid reprojection inputs: missing required input 'input'.");
        }

        if (!TryDecodeGeoJsonDataUri(inputUri, opts.MaxArtifactBytes, out var sourceBytes, out var decodeError))
        {
            Log.InvalidInputs(logger, job.OperationId, decodeError);
            return JobExecutionResult.Failed($"Invalid reprojection inputs: 'input' {decodeError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Both second segments are fixed relative literal filenames, so they can
            // never be rooted and silently discard workspace.
            var inputPath = Path.Join(workspace, "input.geojson");
            var outputPath = Path.Join(workspace, "output.geojson");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running ogr2ogr datum-shift reprojection", cancellationToken).ConfigureAwait(false);

            // -s_srs / -t_srs both EPSG codes: PROJ selects the authoritative
            // transformation pipeline (including a datum/grid shift) for the pair.
            var args = new List<string>
            {
                "-f",
                "GeoJSON",
                "-s_srs",
                $"EPSG:{fromSrid.ToString(CultureInfo.InvariantCulture)}",
                "-t_srs",
                $"EPSG:{toSrid.ToString(CultureInfo.InvariantCulture)}",
                outputPath,
                inputPath,
            };

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("ogr2ogr", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"ogr2ogr timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"ogr2ogr exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed(
                    "ogr2ogr reported success but produced no reprojected FeatureCollection.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding reprojected feature artifact", cancellationToken).ConfigureAwait(false);

            var bounded = await GdalArtifactPublisher.ReadBoundedPayloadAsync(
                outputPath, opts.MaxArtifactBytes, cancellationToken).ConfigureAwait(false);
            if (bounded.ExceededLimit)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, bounded.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Reprojected FeatureCollection size {bounded.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (bounded.Payload is not { Length: > 0 } outputBytes)
            {
                return JobExecutionResult.Failed("ogr2ogr produced an empty reprojected FeatureCollection.");
            }

            var artifactUri = GdalDataUri.Build(GeoJsonContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Reprojection completed", cancellationToken).ConfigureAwait(false);

            Log.ReprojectionCompleted(logger, job.OperationId, fromSrid, toSrid, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static bool TryReadSrid(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out int srid,
        out string error)
    {
        srid = 0;
        error = "";

        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            error = $"missing required input '{name}'";
            return false;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) || srid <= 0)
        {
            error = $"input '{name}' value '{raw}' is not a positive EPSG SRID";
            return false;
        }

        return true;
    }

    private static bool TryDecodeGeoJsonDataUri(
        string value,
        long maxBytes,
        out byte[] bytes,
        out string error)
    {
        bytes = [];
        error = "";

        if (!value.StartsWith(GeoJsonDataUriPrefix, StringComparison.Ordinal))
        {
            error = $"must be a '{GeoJsonDataUriPrefix}' data URI";
            return false;
        }

        var payload = value[GeoJsonDataUriPrefix.Length..];

        // Conservative upper-bound size guard before decoding, mirroring
        // GdalJobInputReader.TryGetBase64Input: base64 packs 4 chars into 3 bytes.
        if ((long)payload.Length / 4 * 3 > maxBytes)
        {
            error = $"payload size exceeds configured MaxArtifactBytes={maxBytes}";
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            error = "payload is not valid base64";
            bytes = [];
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "payload decoded to zero bytes";
            bytes = [];
            return false;
        }

        if (bytes.Length > maxBytes)
        {
            error = $"payload size {bytes.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}";
            bytes = [];
            return false;
        }

        // Refuse a content-sniffed VRT/service-XML indirection blob or an embedded /vsi
        // reference before it is ever staged to scratch and opened by ogr2ogr.
        // This executor strips its own data-URI prefix and decodes inline rather than
        // through the shared GdalJobInputReader chokepoint, so a VRT / OGR VRT / service
        // XML smuggled in as the "GeoJSON" data URI would otherwise reach GDAL unchecked.
        if (!GdalUntrustedInputGuard.IsAdmissible(bytes, out var guardReason))
        {
            error = $"rejected: {guardReason}";
            bytes = [];
            return false;
        }

        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9240, LogLevel.Warning,
            "GDAL vector reproject executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9241, LogLevel.Warning,
            "GDAL vector reproject executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9242, LogLevel.Error,
            "GDAL vector reproject executor failed job {OperationId}: ogr2ogr exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9243, LogLevel.Error,
            "GDAL vector reproject executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9244, LogLevel.Warning,
            "GDAL vector reproject executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9245, LogLevel.Information,
            "GDAL vector reproject executor completed job {OperationId}: fromSrid={FromSrid}, toSrid={ToSrid}, bytes={Bytes}")]
        public static partial void ReprojectionCompleted(ILogger logger, string operationId, int fromSrid, int toSrid, long bytes);
    }
}
