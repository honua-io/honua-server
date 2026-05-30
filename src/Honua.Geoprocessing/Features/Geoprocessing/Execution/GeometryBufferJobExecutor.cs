// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.buffer</c> process.
///
/// This is the first concrete geoprocessing executor wired into the worker host
/// after #1031's discovery that submitted jobs were uniformly abandoned with
/// <c>NoExecutorForKind</c>. It reads the canonical step inputs projected onto
/// <see cref="ExecutionJobSpec.Parameters"/> by <see cref="GeoprocessingJobService"/>,
/// runs <see cref="Geometry.Buffer(double)"/> against the supplied WKB geometry,
/// and publishes the result as a single GeoJSON Feature data URI. Other process
/// ids surfaced through <see cref="ExecutionJobKind.Geoprocessing"/> fail
/// terminally with a clean classification — the dispatcher is intentionally
/// narrow until additional executors land.
/// </summary>
internal sealed partial class GeometryBufferJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog entry in
    /// <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.buffer";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryBufferJobExecutor> _logger;

    public GeometryBufferJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryBufferJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.buffer executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing buffer inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadStepInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid buffer inputs: {inputError}");
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
            return JobExecutionResult.Failed("Invalid buffer inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid buffer inputs: geometry is empty.");
        }

        // The first slice intentionally treats `distance` as units of the input
        // CRS. CRS-aware (geodesic) buffering is classified as manual-review and
        // is rejected here to avoid silently mis-applying meters to a 4326
        // geometry.
        if (inputs.Geodesic)
        {
            return JobExecutionResult.Failed(
                "Geodesic buffering is not yet supported in the first-slice executor; submit with geodesic=false " +
                "and supply distance in the input CRS units.");
        }

        geometry.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing buffer geometry", cancellationToken).ConfigureAwait(false);

        Geometry buffered;
        try
        {
            buffered = geometry.Buffer(inputs.Distance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.BufferComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed(
                $"Buffer computation failed: {ex.GetType().Name}.");
        }

        if (buffered == null || buffered.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Buffer computation produced an empty geometry; verify distance and input geometry.");
        }

        buffered.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding buffer artifact", cancellationToken).ConfigureAwait(false);

        var payload = BuildGeoJsonFeature(buffered, inputs);
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Buffer artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Reduce the buffer distance or simplify the input geometry.");
        }

        var artifactUri = BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Buffer completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadStepInputs(
        IReadOnlyDictionary<string, string> parameters,
        out BufferInputs inputs,
        out string error)
    {
        inputs = default;
        error = "";

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!TryGetPrefixed(parameters, prefix, "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!TryGetPrefixed(parameters, prefix, "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        if (!TryGetPrefixed(parameters, prefix, "distance", out var distanceRaw)
            || !double.TryParse(distanceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
            || double.IsNaN(distance) || double.IsInfinity(distance))
        {
            error = "missing or invalid input 'distance'; expected a finite number";
            return false;
        }

        var geodesic = false;
        if (TryGetPrefixed(parameters, prefix, "geodesic", out var geodesicRaw)
            && !string.IsNullOrWhiteSpace(geodesicRaw)
            && !bool.TryParse(geodesicRaw, out geodesic))
        {
            error = "invalid input 'geodesic'; expected boolean";
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

        inputs = new BufferInputs(wkbBytes, srid, distance, geodesic);
        return true;
    }

    private static bool TryGetPrefixed(
        IReadOnlyDictionary<string, string> parameters,
        string prefix,
        string name,
        out string? value)
    {
        if (parameters.TryGetValue(prefix + name, out var v))
        {
            value = v;
            return true;
        }

        value = null;
        return false;
    }

    private static byte[] BuildGeoJsonFeature(Geometry geometry, BufferInputs inputs)
    {
        // Build the GeoJSON Feature manually so we have a tight, predictable
        // shape that does not echo the raw input WKB into the artifact. The
        // properties record the parameters that shaped the geometry without
        // leaking the source payload byte-for-byte.
        var writer = new GeoJsonWriter();
        var geometryJson = writer.Write(geometry);

        using var buffer = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("type", "Feature");

            jsonWriter.WritePropertyName("geometry");
            using (var doc = JsonDocument.Parse(geometryJson))
            {
                doc.RootElement.WriteTo(jsonWriter);
            }

            jsonWriter.WritePropertyName("properties");
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("processId", HandledProcessId);
            jsonWriter.WriteNumber("inputSrid", inputs.Srid);
            jsonWriter.WriteNumber("bufferDistance", inputs.Distance);
            jsonWriter.WriteEndObject();

            jsonWriter.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string BuildDataUri(byte[] payload)
    {
        var sb = new StringBuilder(payload.Length * 2 + 64);
        sb.Append("data:application/geo+json;base64,");
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private readonly record struct BufferInputs(byte[] WkbBytes, int Srid, double Distance, bool Geodesic);

    private static partial class Log
    {
        [LoggerMessage(9080, LogLevel.Warning,
            "Geometry buffer executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9081, LogLevel.Warning,
            "Geometry buffer executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9082, LogLevel.Warning,
            "Geometry buffer executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9083, LogLevel.Error,
            "Geometry buffer executor failed job {OperationId} during buffer computation")]
        public static partial void BufferComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9084, LogLevel.Warning,
            "Geometry buffer executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
