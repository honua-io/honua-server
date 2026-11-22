// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryBufferJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.buffer";

    // A single Buffer call on a large collection does not observe cancellation,
    // so one cancelled job holds the in-process worker until it finishes.
    // Collections larger than this are buffered in pieces and the token is
    // checked between pieces. One piece still runs to completion.
    internal const int CooperativeChunkSize = 1024;

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryBufferJobExecutor> _logger;

    public GeometryBufferJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryBufferJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.buffer executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing buffer inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadStepInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.InvalidInput($"Invalid buffer inputs: {inputError}");
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
            return JobExecutionResult.InvalidInput("Invalid buffer inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.InvalidInput("Invalid buffer inputs: geometry is empty.");
        }

        if (!GeometrySridGuard.TryValidateEmbeddedSrid(geometry, inputs.Srid, out var sridError))
        {
            Log.InvalidInputs(_logger, job.OperationId, sridError);
            return JobExecutionResult.InvalidInput($"Invalid buffer inputs: {sridError}.");
        }

        geometry.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing buffer geometry", cancellationToken).ConfigureAwait(false);

        Geometry buffered;
        try
        {
            buffered = inputs.Geodesic && GeodesicGeometryOperations.IsGeographicSrid(inputs.Srid)
                ? GeodesicGeometryOperations.BufferGeodesic(geometry, inputs.Distance * inputs.MetersPerUnit, cancellationToken)
                : BufferCooperatively(geometry, inputs.Geodesic ? inputs.Distance * inputs.MetersPerUnit : inputs.Distance, cancellationToken);
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

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Buffer completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    internal sealed class BufferCooperativeRequest
    {
        public required Geometry Geometry { get; init; }

        public required double Distance { get; init; }

        public required CancellationToken CancellationToken { get; init; }

        public int ChunkSize { get; init; } = CooperativeChunkSize;

        public Action<int>? AfterChunk { get; init; }
    }

    internal static Geometry BufferCooperatively(
        Geometry geometry,
        double distance,
        CancellationToken cancellationToken)
        => BufferCooperatively(new BufferCooperativeRequest
        {
            Geometry = geometry,
            Distance = distance,
            CancellationToken = cancellationToken,
        });

    internal static Geometry BufferCooperatively(BufferCooperativeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ChunkSize);
        request.CancellationToken.ThrowIfCancellationRequested();

        var geometry = request.Geometry;
        if (geometry.NumGeometries <= request.ChunkSize)
        {
            var single = geometry.Buffer(request.Distance);
            request.AfterChunk?.Invoke(1);
            return single;
        }

        return UnionBufferedChunks(geometry, CollectBufferedChunks(request));
    }

    private static List<Geometry> CollectBufferedChunks(BufferCooperativeRequest request)
    {
        var geometry = request.Geometry;
        var count = geometry.NumGeometries;
        var parts = new List<Geometry>();
        var index = 0;
        var chunks = 0;
        while (index < count)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(request.ChunkSize, count - index);
            var batch = new Geometry[take];
            for (var i = 0; i < take; i++)
            {
                batch[i] = geometry.GetGeometryN(index + i);
            }

            index += take;
            var buffered = geometry.Factory.BuildGeometry(batch).Buffer(request.Distance);
            if (buffered is { IsEmpty: false })
            {
                parts.Add(buffered);
            }

            chunks++;
            request.AfterChunk?.Invoke(chunks);
        }

        request.CancellationToken.ThrowIfCancellationRequested();
        return parts;
    }

    private static Geometry UnionBufferedChunks(Geometry geometry, List<Geometry> parts)
    {
        if (parts.Count == 0)
        {
            return geometry.Factory.CreateEmpty((Dimension)geometry.Dimension);
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        return UnaryUnionOp.Union(parts);
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

        // A buffer requires a strictly positive distance. Zero or negative
        // distances erode NTS geometries to empty; rejecting them here surfaces
        // an accurate input-validation error instead of the misleading late
        // "produced an empty geometry" failure.
        if (distance <= 0d)
        {
            error = "invalid input 'distance'; expected a finite number greater than zero";
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

        var unitRaw = TryGetPrefixed(parameters, prefix, "unit", out var rawUnit) ? rawUnit : null;
        if (!GeodesicGeometryOperations.TryNormalizeLinearUnit(unitRaw, out _, out var metersPerUnit))
        {
            error = $"invalid input 'unit' '{unitRaw}'; expected meters, kilometers, feet, yards, miles, or nautical-miles";
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

        inputs = new BufferInputs(wkbBytes, srid, distance, geodesic, metersPerUnit);
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
        var writer = GeoJsonArtifactCodec.CreateWriter();
        var geometryJson = writer.Write(geometry);

        using var buffer = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("type", "Feature");
            new FeatureArtifactSpatialReference("known", inputs.Srid).Write(jsonWriter);

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

    private readonly record struct BufferInputs(byte[] WkbBytes, int Srid, double Distance, bool Geodesic, double MetersPerUnit);

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
