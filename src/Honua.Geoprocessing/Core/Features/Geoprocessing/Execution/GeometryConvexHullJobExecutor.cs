// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryConvexHullJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.convex-hull";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryConvexHullJobExecutor> _logger;

    public GeometryConvexHullJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryConvexHullJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.convex-hull executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing convex-hull inputs", cancellationToken).ConfigureAwait(false);

        var inputRead = GeometryExecutorSupport.ReadSingleGeometryInputs(parameters);
        if (!inputRead.IsValid)
        {
            Log.InvalidInputs(_logger, job.OperationId, inputRead.Error!);
            return JobExecutionResult.InvalidInput($"Invalid convex-hull inputs: {inputRead.Error}");
        }

        var inputs = inputRead.Inputs;
        var decodeResult = GeometryExecutorSupport.DecodeWkbAndValidateSrid(inputs.WkbBytes, inputs.Srid);
        if (!decodeResult.IsValid)
        {
            if (decodeResult.FailureKind == WkbDecodeFailureKind.InvalidWkb)
            {
                Log.InvalidWkb(_logger, job.OperationId, decodeResult.Error!);
                return JobExecutionResult.InvalidInput("Invalid convex-hull inputs: WKB payload could not be decoded.");
            }

            if (decodeResult.FailureKind == WkbDecodeFailureKind.EmptyGeometry)
            {
                return JobExecutionResult.InvalidInput("Invalid convex-hull inputs: geometry is empty.");
            }

            Log.InvalidInputs(_logger, job.OperationId, decodeResult.Error!);
            return JobExecutionResult.InvalidInput($"Invalid convex-hull inputs: {decodeResult.Error}.");
        }

        var geometry = decodeResult.Geometry!;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing convex hull", cancellationToken).ConfigureAwait(false);

        Geometry hull;
        try
        {
            // NTS ConvexHull handles all geometry types uniformly: for
            // collections / multi-geometries it produces the hull over all
            // member vertices, matching PostGIS ST_ConvexHull semantics.
            hull = geometry.ConvexHull();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.HullComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Convex-hull computation failed: {ex.GetType().Name}.");
        }

        if (hull == null || hull.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Convex-hull computation produced an empty geometry; verify the input geometry.");
        }

        hull.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding convex-hull artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            hull,
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
                $"Convex-hull artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Convex-hull completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static partial class Log
    {
        [LoggerMessage(9170, LogLevel.Warning,
            "Geometry convex-hull executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9171, LogLevel.Warning,
            "Geometry convex-hull executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9172, LogLevel.Warning,
            "Geometry convex-hull executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9173, LogLevel.Error,
            "Geometry convex-hull executor failed job {OperationId} during hull computation")]
        public static partial void HullComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9174, LogLevel.Warning,
            "Geometry convex-hull executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
