// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.centroid</c>
/// process. Slice 4 of #1031 — extends the buffer / clip / intersect /
/// project / area / union set with a per-feature centroid output.
///
/// Computes the centroid of the supplied geometry using NetTopologySuite's
/// <see cref="Geometry.Centroid"/> primitive — a deterministic, robust
/// centroid that handles point / line / polygon inputs uniformly — and
/// publishes the result as a single GeoJSON Point Feature on the
/// <c>outputFeatureLayer</c> slot. The output is always a point regardless
/// of the input geometry type, matching the long-standing GeoServices and
/// PostGIS <c>ST_Centroid</c> semantics.
/// </summary>
internal sealed partial class GeometryCentroidJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
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

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid centroid inputs: {inputError}");
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
            return JobExecutionResult.Failed("Invalid centroid inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid centroid inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;

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

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out CentroidInputs inputs,
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

        inputs = new CentroidInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    private readonly record struct CentroidInputs(byte[] WkbBytes, int Srid);

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
