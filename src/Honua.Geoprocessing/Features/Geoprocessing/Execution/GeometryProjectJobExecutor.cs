// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Infrastructure.Rendering;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.project</c>
/// process. Slice 2 of #1031 — companion to <see cref="GeometryBufferJobExecutor"/>.
///
/// Reprojects an input geometry between SRIDs that are supported by the
/// in-memory <see cref="CoordinateTransformer"/> path (identity, Web
/// Mercator aliases, and WGS 84 ↔ Web Mercator). Datum-shift transforms
/// requiring <c>ST_Transform</c> are explicitly rejected here so the
/// first-slice executor remains deterministic without a PostGIS round-trip;
/// those pairs are tracked as a follow-on.
/// </summary>
internal sealed partial class GeometryProjectJobExecutor : IJobExecutor
{
    internal const string HandledProcessId = "geometry.project";

    private static readonly HashSet<int> WebMercatorAliases =
        new() { 3857, 900913, 102100, 102113, 3785 };

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryProjectJobExecutor> _logger;

    public GeometryProjectJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryProjectJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.project executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing project inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid project inputs: {inputError}");
        }

        if (!IsTransformSupported(inputs.FromSrid, inputs.ToSrid))
        {
            return JobExecutionResult.Failed(
                $"Project transform from SRID {inputs.FromSrid} to {inputs.ToSrid} is not supported by " +
                "the first-slice in-memory transform path. Supported pairs: identity, Web Mercator aliases " +
                "(3857/900913/102100/102113/3785), and WGS 84 (4326) ↔ Web Mercator. Datum-shift pairs that " +
                "require ST_Transform are deferred to a follow-on slice.");
        }

        Geometry source;
        try
        {
            var reader = new WKBReader { HandleSRID = true };
            source = reader.Read(inputs.WkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, ex.Message);
            return JobExecutionResult.Failed("Invalid project inputs: WKB payload could not be decoded.");
        }

        if (source == null || source.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid project inputs: geometry is empty.");
        }

        source.SRID = inputs.FromSrid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing reprojected geometry", cancellationToken).ConfigureAwait(false);

        Geometry projected;
        try
        {
            projected = inputs.FromSrid == inputs.ToSrid
                || (WebMercatorAliases.Contains(inputs.FromSrid) && WebMercatorAliases.Contains(inputs.ToSrid))
                    ? source.Copy()
                    : ReprojectInMemory(source, inputs.FromSrid, inputs.ToSrid);
        }
        catch (NotSupportedException nse)
        {
            // CoordinateTransformer.TransformPoint throws NotSupportedException for
            // unhandled SRID pairs; surface that as a clean classified failure.
            return JobExecutionResult.Failed($"Project transform unavailable: {nse.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ProjectComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Project computation failed: {ex.GetType().Name}.");
        }

        if (projected == null || projected.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Project computation produced an empty geometry; verify the input.");
        }

        projected.SRID = inputs.ToSrid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding project artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            projected,
            HandledProcessId,
            new[]
            {
                ("fromSrid", (object)inputs.FromSrid),
                ("toSrid", (object)inputs.ToSrid),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Project artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Simplify the input geometry before reprojecting.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Project completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool IsTransformSupported(int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return true;
        }

        if (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (WebMercatorAliases.Contains(fromSrid) && toSrid == 4326)
        {
            return true;
        }

        return false;
    }

    private static Geometry ReprojectInMemory(Geometry source, int fromSrid, int toSrid)
    {
        // Use a GeometryEditor to walk every coordinate via the in-memory
        // CoordinateTransformer; this preserves geometry type, ordering, and
        // ring structure without allocating per-vertex geometries.
        var editor = new GeometryEditor(source.Factory);
        var transformed = editor.Edit(source, new CoordinateOperation(fromSrid, toSrid));
        return transformed;
    }

    private sealed class CoordinateOperation(int fromSrid, int toSrid) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var transformed = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var original = coordinates[i];
                var (x, y) = CoordinateTransformer.TransformPoint(original.X, original.Y, fromSrid, toSrid);
                transformed[i] = new Coordinate(x, y);
            }

            return transformed;
        }
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out ProjectInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "fromSrid", out var fromRaw)
            || !int.TryParse(fromRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromSrid)
            || fromSrid <= 0)
        {
            error = "missing or invalid input 'fromSrid'; expected a positive integer";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "toSrid", out var toRaw)
            || !int.TryParse(toRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var toSrid)
            || toSrid <= 0)
        {
            error = "missing or invalid input 'toSrid'; expected a positive integer";
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

        inputs = new ProjectInputs(wkbBytes, fromSrid, toSrid);
        error = "";
        return true;
    }

    private readonly record struct ProjectInputs(byte[] WkbBytes, int FromSrid, int ToSrid);

    private static partial class Log
    {
        [LoggerMessage(9120, LogLevel.Warning,
            "Geometry project executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9121, LogLevel.Warning,
            "Geometry project executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9122, LogLevel.Warning,
            "Geometry project executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9123, LogLevel.Error,
            "Geometry project executor failed job {OperationId} during reprojection")]
        public static partial void ProjectComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9124, LogLevel.Warning,
            "Geometry project executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
