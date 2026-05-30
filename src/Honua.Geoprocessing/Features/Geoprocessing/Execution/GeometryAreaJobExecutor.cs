// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.area</c> process.
/// Slice 3 of #1031 — extends the buffer / clip / intersect / project set
/// with a per-feature measure operation.
///
/// Computes the planar (Cartesian) area of the supplied geometry in input-CRS
/// units squared and publishes a single value-result artifact. The published
/// document is the canonical measure-result JSON shape: a single object with
/// the process id, input SRID, geometry type, and a numeric <c>value</c>.
/// Geodesic area (square meters on the WGS 84 ellipsoid) is documented in the
/// catalog but is rejected here — the in-process first-slice executor avoids
/// pulling a GeographicLib dependency for the initial executor set; the
/// geodesic path is tracked as a follow-on slice alongside the geodesic
/// buffer / length executors.
/// </summary>
internal sealed partial class GeometryAreaJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog entry
    /// in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.area";

    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryAreaJobExecutor> _logger;

    public GeometryAreaJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryAreaJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.area executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing area inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid area inputs: {inputError}");
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
            return JobExecutionResult.Failed("Invalid area inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid area inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing area", cancellationToken).ConfigureAwait(false);

        double area;
        try
        {
            // First-slice executor computes the planar area in the units of the
            // input CRS. Geodesic area (catalog-documented square meters) is
            // rejected as out-of-scope until the geodesic measure slice lands;
            // this keeps the deterministic measurement consistent with the
            // first-slice buffer executor's planar-only behavior.
            area = geometry.Area;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AreaComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Area computation failed: {ex.GetType().Name}.");
        }

        if (double.IsNaN(area) || double.IsInfinity(area))
        {
            return JobExecutionResult.Failed(
                "Area computation produced a non-finite value; verify the input geometry coordinates.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding area artifact", cancellationToken).ConfigureAwait(false);

        var payload = BuildMeasurePayload(area, geometry, inputs);
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Area artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Area completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out AreaInputs inputs,
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

        inputs = new AreaInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    private static byte[] BuildMeasurePayload(double area, Geometry geometry, AreaInputs inputs)
    {
        // Canonical measure-result envelope: a stable JSON document that
        // OGC API Processes / GeoServices clients can read uniformly. The
        // shape mirrors the GeoJSON Feature properties bag used by the
        // FeatureLayer executors, minus a geometry, so downstream tooling can
        // share the same property reader.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MeasureResult");
            writer.WriteString("processId", HandledProcessId);
            writer.WriteString("measure", "area");
            writer.WriteNumber("value", area);
            writer.WriteString("unit", "input-crs-units-squared");
            writer.WriteNumber("inputSrid", inputs.Srid);
            writer.WriteString("inputGeometryType", geometry.GeometryType);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string BuildDataUri(byte[] payload)
    {
        var sb = new StringBuilder(payload.Length * 2 + ScalarDataUriPrefix.Length);
        sb.Append(ScalarDataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private readonly record struct AreaInputs(byte[] WkbBytes, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9130, LogLevel.Warning,
            "Geometry area executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9131, LogLevel.Warning,
            "Geometry area executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9132, LogLevel.Warning,
            "Geometry area executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9133, LogLevel.Error,
            "Geometry area executor failed job {OperationId} during area computation")]
        public static partial void AreaComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9134, LogLevel.Warning,
            "Geometry area executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
