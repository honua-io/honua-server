// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.intersect</c>
/// process. Slice 2 of #1031 — companion to <see cref="GeometryBufferJobExecutor"/>.
///
/// Computes the geometric intersection of a target geometry with an
/// intersector geometry. Both geometries must share the supplied SRID;
/// reprojection is handled by <see cref="GeometryProjectJobExecutor"/>.
/// </summary>
internal sealed partial class GeometryIntersectJobExecutor : IJobExecutor
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

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid intersect inputs: {inputError}");
        }

        if (!TryDecodeGeometry(inputs.TargetWkb, out var target, out var decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "targetWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid intersect inputs: targetWkb {decodeError}");
        }

        if (!TryDecodeGeometry(inputs.IntersectorWkb, out var intersector, out decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "intersectorWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid intersect inputs: intersectorWkb {decodeError}");
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

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out IntersectInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "targetWkb", out var target) || string.IsNullOrWhiteSpace(target))
        {
            error = "missing required input 'targetWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "intersectorWkb", out var intersector) || string.IsNullOrWhiteSpace(intersector))
        {
            error = "missing required input 'intersectorWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        inputs = new IntersectInputs(target, intersector, srid);
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

    private readonly record struct IntersectInputs(string TargetWkb, string IntersectorWkb, int Srid);

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
