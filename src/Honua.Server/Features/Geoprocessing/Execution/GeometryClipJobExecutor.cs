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

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.clip</c> process.
/// Slice 2 of #1031 — companion to <see cref="GeometryBufferJobExecutor"/>.
///
/// Clips a target geometry to the axis-aligned bounding envelope of a
/// supplied clip geometry. The catalog declares the clip parameter as
/// <c>clipEnvelopeWkb</c> and explicitly states that the bounding envelope
/// is used (not the polygon itself), so this executor materializes the
/// clip envelope and intersects the target with it.
/// </summary>
internal sealed partial class GeometryClipJobExecutor : IJobExecutor
{
    internal const string HandledProcessId = "geometry.clip";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryClipJobExecutor> _logger;

    public GeometryClipJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryClipJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.clip executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing clip inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid clip inputs: {inputError}");
        }

        if (!TryDecodeGeometry(inputs.TargetWkb, out var target, out var decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "targetWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid clip inputs: targetWkb {decodeError}");
        }

        if (!TryDecodeGeometry(inputs.ClipEnvelopeWkb, out var clip, out decodeError))
        {
            Log.InvalidWkb(_logger, job.OperationId, "clipEnvelopeWkb", decodeError);
            return JobExecutionResult.Failed($"Invalid clip inputs: clipEnvelopeWkb {decodeError}");
        }

        target.SRID = inputs.Srid;
        clip.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing clip geometry", cancellationToken).ConfigureAwait(false);

        Geometry clipped;
        try
        {
            // The catalog parameter name is `clipEnvelopeWkb` and the description
            // explicitly says "bounding envelope is used". Materialize the
            // envelope as a polygon to keep the intersection deterministic
            // regardless of the input geometry shape.
            var envelope = clip.EnvelopeInternal;
            if (envelope == null || envelope.IsNull)
            {
                return JobExecutionResult.Failed(
                    "Invalid clip inputs: clipEnvelopeWkb has no envelope.");
            }

            var envelopeGeometry = target.Factory.ToGeometry(envelope);
            envelopeGeometry.SRID = inputs.Srid;
            clipped = target.Intersection(envelopeGeometry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ClipComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Clip computation failed: {ex.GetType().Name}.");
        }

        if (clipped == null || clipped.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Clip computation produced an empty geometry; verify that the target intersects the clip envelope.");
        }

        clipped.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding clip artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            clipped,
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
                $"Clip artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Reduce the target geometry size or pre-filter it before clipping.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Clip completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out ClipInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "targetWkb", out var target) || string.IsNullOrWhiteSpace(target))
        {
            error = "missing required input 'targetWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "clipEnvelopeWkb", out var clip) || string.IsNullOrWhiteSpace(clip))
        {
            error = "missing required input 'clipEnvelopeWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        inputs = new ClipInputs(target, clip, srid);
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

    private readonly record struct ClipInputs(string TargetWkb, string ClipEnvelopeWkb, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9100, LogLevel.Warning,
            "Geometry clip executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9101, LogLevel.Warning,
            "Geometry clip executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9102, LogLevel.Warning,
            "Geometry clip executor rejected job {OperationId}: WKB decode failed for {Field}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string field, string reason);

        [LoggerMessage(9103, LogLevel.Error,
            "Geometry clip executor failed job {OperationId} during clip computation")]
        public static partial void ClipComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9104, LogLevel.Warning,
            "Geometry clip executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}

/// <summary>
/// Shared GeoJSON Feature writer for the slice 2 vector executors. Centralizes
/// the artifact payload shape so the dispatcher, OGC API Processes, and
/// GeoServices GPServer surfaces see a uniform document.
/// </summary>
internal static class GeometryFeatureWriter
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    public static byte[] WriteFeature(
        Geometry geometry,
        string processId,
        IEnumerable<(string Name, object Value)> properties)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(properties);

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
            jsonWriter.WriteString("processId", processId);
            foreach (var (name, value) in properties)
            {
                WriteJsonProperty(jsonWriter, name, value);
            }
            jsonWriter.WriteEndObject();

            jsonWriter.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string BuildDataUri(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var sb = new StringBuilder(payload.Length * 2 + DataUriPrefix.Length);
        sb.Append(DataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static void WriteJsonProperty(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            default:
                writer.WriteString(name, value?.ToString() ?? string.Empty);
                break;
        }
    }
}
