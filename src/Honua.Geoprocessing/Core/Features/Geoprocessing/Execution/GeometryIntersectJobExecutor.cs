// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryIntersectJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.intersect";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryIntersectJobExecutor> _logger;

    public GeometryIntersectJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryIntersectJobExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.intersect executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing intersect inputs", cancellationToken).ConfigureAwait(false);

        if (!GeometryExecutorSupport.TryReadPairGeometryInputs(parameters, "targetWkb", "intersectorWkb", out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.InvalidInput($"Invalid intersect inputs: {inputError}");
        }

        if (!GeometryExecutorSupport.TryDecodeGeometry(inputs.FirstWkb, out var target, out var decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "targetWkb", decodeError);
            return JobExecutionResult.InvalidInput($"Invalid intersect inputs: targetWkb {decodeError}");
        }

        if (!GeometryExecutorSupport.TryDecodeGeometry(inputs.SecondWkb, out var intersector, out decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "intersectorWkb", decodeError);
            return JobExecutionResult.InvalidInput($"Invalid intersect inputs: intersectorWkb {decodeError}");
        }

        if (!GeometrySridGuard.TryValidateEmbeddedSrid(target, inputs.Srid, out var targetSridError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "targetWkb", targetSridError);
            return JobExecutionResult.InvalidInput($"Invalid intersect inputs: targetWkb {targetSridError}.");
        }

        if (!GeometrySridGuard.TryValidateEmbeddedSrid(intersector, inputs.Srid, out var intersectorSridError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "intersectorWkb", intersectorSridError);
            return JobExecutionResult.InvalidInput($"Invalid intersect inputs: intersectorWkb {intersectorSridError}.");
        }

        target.SRID = inputs.Srid;
        intersector.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing intersect geometry", cancellationToken).ConfigureAwait(false);

        Geometry intersection;
        try
        {
            intersection = target.Intersection(intersector);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.IntersectComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Intersect computation failed: {ex.GetType().Name}.");
        }

        if (intersection == null || intersection.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Intersect computation produced an empty geometry; verify that the two geometries overlap.");
        }

        intersection.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding intersect artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            intersection,
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
                $"Intersect artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Simplify the inputs or pre-clip before intersecting.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Intersect completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static partial class Log
    {
        [LoggerMessage(9110, LogLevel.Warning,
            "Geometry intersect executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9111, LogLevel.Warning,
            "Geometry intersect executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9112, LogLevel.Warning,
            "Geometry intersect executor rejected job {OperationId}: WKB decode failed for {Field}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string field, string reason);

        [LoggerMessage(9113, LogLevel.Error,
            "Geometry intersect executor failed job {OperationId} during intersect computation")]
        public static partial void IntersectComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9114, LogLevel.Warning,
            "Geometry intersect executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
