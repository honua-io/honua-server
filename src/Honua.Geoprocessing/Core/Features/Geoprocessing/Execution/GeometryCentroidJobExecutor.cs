// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryCentroidJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.centroid";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryCentroidJobExecutor> _logger;

    public GeometryCentroidJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryCentroidJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.centroid executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing centroid inputs", cancellationToken).ConfigureAwait(false);

        if (!GeometryExecutorSupport.TryReadSingleGeometryInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.InvalidInput($"Invalid centroid inputs: {inputError}");
        }

        if (!GeometryExecutorSupport.TryDecodeWkbAndValidateSrid(
            inputs.WkbBytes,
            inputs.Srid,
            out var geometry,
            out var wkbError,
            out var isEmpty,
            out var sridError))
        {
            if (wkbError is not null)
            {
                Log.InvalidWkb(_logger, job.OperationId, wkbError);
                return JobExecutionResult.InvalidInput("Invalid centroid inputs: WKB payload could not be decoded.");
            }

            if (isEmpty)
            {
                return JobExecutionResult.InvalidInput("Invalid centroid inputs: geometry is empty.");
            }

            Log.InvalidInputs(_logger, job.OperationId, sridError!);
            return JobExecutionResult.InvalidInput($"Invalid centroid inputs: {sridError}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing centroid", cancellationToken).ConfigureAwait(false);

        Geometry centroid;
        try
        {
            centroid = geometry.Centroid;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.CentroidComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Centroid computation failed: {ex.GetType().Name}.");
        }

        if (centroid == null || centroid.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Centroid computation produced an empty geometry; verify the input geometry.");
        }

        centroid.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding centroid artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            centroid,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
                ("inputGeometryType", (object)geometry.GeometryType),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Centroid artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Centroid completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static partial class Log
    {
        [LoggerMessage(9150, LogLevel.Warning,
            "Geometry centroid executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9151, LogLevel.Warning,
            "Geometry centroid executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9152, LogLevel.Warning,
            "Geometry centroid executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9153, LogLevel.Error,
            "Geometry centroid executor failed job {OperationId} during centroid computation")]
        public static partial void CentroidComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9154, LogLevel.Warning,
            "Geometry centroid executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
