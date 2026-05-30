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
/// Production <see cref="IJobExecutor"/> for the <c>geometry.length</c>
/// process. Slice 4 of #1031 — companion to <see cref="GeometryAreaJobExecutor"/>
/// for one-dimensional measure outputs.
///
/// Computes the planar (Cartesian) length of the supplied geometry in
/// input-CRS units and publishes a single canonical <c>MeasureResult</c>
/// document on the <c>outputScalar</c> slot. Geodesic length (meters on the
/// WGS 84 ellipsoid) is catalog-documented but deferred to the geodesic
/// measure slice, mirroring how <see cref="GeometryAreaJobExecutor"/>
/// rejects geodesic until that follow-on lands. Returns the
/// <see cref="Geometry.Length"/> for line geometries; for polygons NTS
/// returns the perimeter, which is consistent with the catalog description.
/// </summary>
internal sealed partial class GeometryLengthJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.length";

    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryLengthJobExecutor> _logger;

    public GeometryLengthJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryLengthJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.length executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing length inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid length inputs: {inputError}");
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
            return JobExecutionResult.Failed("Invalid length inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid length inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing length", cancellationToken).ConfigureAwait(false);

        double length;
        try
        {
            // First-slice executor computes planar length in input-CRS units.
            // Geodesic length is documented in the catalog but deferred to
            // the geodesic measure slice, matching geometry.area's policy.
            length = geometry.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LengthComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Length computation failed: {ex.GetType().Name}.");
        }

        if (double.IsNaN(length) || double.IsInfinity(length))
        {
            return JobExecutionResult.Failed(
                "Length computation produced a non-finite value; verify the input geometry coordinates.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding length artifact", cancellationToken).ConfigureAwait(false);

        var payload = BuildMeasurePayload(length, geometry, inputs);
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Length artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Length completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out LengthInputs inputs,
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

        inputs = new LengthInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    private static byte[] BuildMeasurePayload(double length, Geometry geometry, LengthInputs inputs)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MeasureResult");
            writer.WriteString("processId", HandledProcessId);
            writer.WriteString("measure", "length");
            writer.WriteNumber("value", length);
            writer.WriteString("unit", "input-crs-units");
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

    private readonly record struct LengthInputs(byte[] WkbBytes, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9160, LogLevel.Warning,
            "Geometry length executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9161, LogLevel.Warning,
            "Geometry length executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9162, LogLevel.Warning,
            "Geometry length executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9163, LogLevel.Error,
            "Geometry length executor failed job {OperationId} during length computation")]
        public static partial void LengthComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9164, LogLevel.Warning,
            "Geometry length executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
