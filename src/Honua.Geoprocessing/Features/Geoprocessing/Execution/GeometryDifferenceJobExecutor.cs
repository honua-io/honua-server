// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.difference</c>
/// process. Closes a catalog-honesty gap on trunk: the process was advertised
/// in <see cref="BuiltInProcessCatalog"/> (and flagged synchronous by
/// <c>GPServerExecutionPolicy</c> / first-slice automated by
/// <c>ProcessMigrationEvidenceClassifier</c>) but had no job executor, so a
/// workflow/codemod submitting it as a job could never reach it.
///
/// Subtracts an eraser geometry from a target geometry, returning the remaining
/// portion (<see cref="Geometry.Difference(Geometry)"/>) — the managed
/// NetTopologySuite analogue of PostGIS <c>ST_Difference</c>. Companion to
/// <see cref="GeometryIntersectJobExecutor"/>: both geometries must share the
/// supplied SRID; reprojection is handled by <see cref="GeometryProjectJobExecutor"/>.
/// </summary>
internal sealed partial class GeometryDifferenceJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.difference";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryDifferenceJobExecutor> _logger;

    public GeometryDifferenceJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryDifferenceJobExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.difference executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing difference inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid difference inputs: {inputError}");
        }

        if (!TryDecodeGeometry(inputs.TargetWkb, out var target, out var decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "targetWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid difference inputs: targetWkb {decodeError}");
        }

        if (!TryDecodeGeometry(inputs.EraserWkb, out var eraser, out decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "eraserWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid difference inputs: eraserWkb {decodeError}");
        }

        target.SRID = inputs.Srid;
        eraser.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing difference geometry", cancellationToken).ConfigureAwait(false);

        Geometry difference;
        try
        {
            difference = target.Difference(eraser);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.DifferenceComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Difference computation failed: {ex.GetType().Name}.");
        }

        if (difference == null || difference.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Difference computation produced an empty geometry; the eraser may fully cover the target.");
        }

        difference.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding difference artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            difference,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Difference artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Simplify the inputs before differencing.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Difference completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out DifferenceInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "targetWkb", out var target) || string.IsNullOrWhiteSpace(target))
        {
            error = "missing required input 'targetWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "eraserWkb", out var eraser) || string.IsNullOrWhiteSpace(eraser))
        {
            error = "missing required input 'eraserWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        inputs = new DifferenceInputs(target, eraser, srid);
        error = "";
        return true;
    }

    private static bool TryDecodeGeometry(string base64Wkb, out Geometry geometry, out string error)
    {
        geometry = null!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Wkb);
        }
        catch (FormatException)
        {
            error = "is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "decoded to zero bytes";
            return false;
        }

        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(bytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            error = $"could not be decoded ({ex.GetType().Name})";
            return false;
        }

        if (geometry == null || geometry.IsEmpty)
        {
            error = "decoded to an empty geometry";
            return false;
        }

        error = "";
        return true;
    }

    private readonly record struct DifferenceInputs(string TargetWkb, string EraserWkb, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9220, LogLevel.Warning,
            "Geometry difference executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9221, LogLevel.Warning,
            "Geometry difference executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9222, LogLevel.Warning,
            "Geometry difference executor rejected job {OperationId}: WKB decode failed for {Field}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string field, string reason);

        [LoggerMessage(9223, LogLevel.Error,
            "Geometry difference executor failed job {OperationId} during difference computation")]
        public static partial void DifferenceComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9224, LogLevel.Warning,
            "Geometry difference executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
