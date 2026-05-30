// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.convex-hull</c>
/// process. Slice 4 of #1031 — completes the per-feature geometry-output
/// trio alongside <see cref="GeometryCentroidJobExecutor"/> and
/// <see cref="GeometryLengthJobExecutor"/>.
///
/// Computes the convex hull of the supplied geometry using
/// <see cref="Geometry.ConvexHull"/> — NetTopologySuite's Graham-scan based
/// implementation — and publishes the result as a single GeoJSON Feature on
/// the <c>outputFeatureLayer</c> slot. For a single point the hull is the
/// point; for two distinct points it is the connecting line; for three or
/// more non-collinear points it is the bounding polygon. Geometry
/// collections / multi-geometries are accepted: NTS treats the hull as the
/// hull over all member vertices, which is the canonical "hull of the
/// union" behavior used by PostGIS <c>ST_ConvexHull</c>.
/// </summary>
internal sealed partial class GeometryConvexHullJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
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

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid convex-hull inputs: {inputError}");
        }

        Geometry geometry;
        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(inputs.WkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, ex.Message);
            return JobExecutionResult.Failed("Invalid convex-hull inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid convex-hull inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;

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

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out HullInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        byte[] wkbBytes;
        try
        {
            wkbBytes = Convert.FromBase64String(wkb);
        }
        catch (FormatException)
        {
            error = "input 'wkb' is not valid base64";
            return false;
        }

        if (wkbBytes.Length == 0)
        {
            error = "input 'wkb' decoded to zero bytes";
            return false;
        }

        inputs = new HullInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    private readonly record struct HullInputs(byte[] WkbBytes, int Srid);

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
