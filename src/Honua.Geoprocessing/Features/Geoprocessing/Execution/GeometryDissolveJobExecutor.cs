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
using NetTopologySuite.Operation.Union;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.dissolve</c>
/// process. Slice 5 of #1031 — extends the prior aggregate-output set
/// (geometry.union) with a group-aware dissolve that collapses adjacent
/// same-attribute geometries into one feature per group.
///
/// Where <see cref="GeometryUnionJobExecutor"/> always emits a single
/// merged Feature, dissolve preserves grouping: callers supply a parallel
/// <c>groupKeys</c> array (one key per input WKB) and the executor unions
/// the geometries within each group, publishing a GeoJSON
/// <c>FeatureCollection</c> with one Feature per distinct key. When
/// <c>groupKeys</c> is omitted, every input collapses into a single
/// group ("__all__") — the same behavior as a plain union but routed
/// through the dissolve artifact shape so downstream consumers can rely
/// on the FeatureCollection envelope.
/// </summary>
internal sealed partial class GeometryDissolveJobExecutor : IJobExecutor
{
    private const string FeatureCollectionDataUriPrefix = "data:application/geo+json;base64,";
    private const string DefaultGroupKey = "__all__";

    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.dissolve";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryDissolveJobExecutor> _logger;

    public GeometryDissolveJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryDissolveJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.dissolve executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing dissolve inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid dissolve inputs: {inputError}");
        }

        var reader = new WKBReader { HandleSRID = true };
        var groupedGeometries = new Dictionary<string, List<Geometry>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        for (var i = 0; i < inputs.WkbItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(inputs.WkbItems[i]);
            }
            catch (FormatException)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "is not valid base64");
                return JobExecutionResult.Failed(
                    $"Invalid dissolve inputs: wkbs[{i}] is not valid base64.");
            }

            if (bytes.Length == 0)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "decoded to zero bytes");
                return JobExecutionResult.Failed(
                    $"Invalid dissolve inputs: wkbs[{i}] decoded to zero bytes.");
            }

            Geometry geometry;
            try
            {
                geometry = reader.Read(bytes);
            }
            catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, $"could not be decoded ({ex.GetType().Name})");
                return JobExecutionResult.Failed(
                    $"Invalid dissolve inputs: wkbs[{i}] could not be decoded.");
            }

            if (geometry == null || geometry.IsEmpty)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "decoded to an empty geometry");
                return JobExecutionResult.Failed(
                    $"Invalid dissolve inputs: wkbs[{i}] decoded to an empty geometry.");
            }

            geometry.SRID = inputs.Srid;

            var key = inputs.GroupKeys?[i] ?? DefaultGroupKey;
            if (!groupedGeometries.TryGetValue(key, out var bucket))
            {
                bucket = new List<Geometry>();
                groupedGeometries[key] = bucket;
                orderedKeys.Add(key);
            }

            bucket.Add(geometry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(45, "Dissolving grouped geometries", cancellationToken).ConfigureAwait(false);

        var dissolved = new List<(string Key, Geometry Geometry)>(orderedKeys.Count);
        foreach (var key in orderedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Geometry unioned;
            try
            {
                // UnaryUnionOp handles mixed geometry types and avoids the
                // O(N) pairwise Union calls that amplify floating-point error
                // across larger collections — matching the union executor.
                unioned = UnaryUnionOp.Union(groupedGeometries[key]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.DissolveComputationFailed(_logger, job.OperationId, key, ex);
                return JobExecutionResult.Failed(
                    $"Dissolve computation failed for group '{key}': {ex.GetType().Name}.");
            }

            if (unioned == null || unioned.IsEmpty)
            {
                return JobExecutionResult.Failed(
                    $"Dissolve computation produced an empty geometry for group '{key}'.");
            }

            unioned.SRID = inputs.Srid;
            dissolved.Add((key, unioned));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding dissolve artifact", cancellationToken).ConfigureAwait(false);

        var payload = WriteFeatureCollection(dissolved, inputs.Srid, inputs.WkbItems.Count);

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Dissolve artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Reduce the input collection size or simplify the inputs before dissolving.");
        }

        var artifactUri = BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Dissolve completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static byte[] WriteFeatureCollection(
        List<(string Key, Geometry Geometry)> dissolved,
        int srid,
        int inputCount)
    {
        var writer = new GeoJsonWriter();

        using var buffer = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("type", "FeatureCollection");
            jsonWriter.WriteString("processId", HandledProcessId);
            jsonWriter.WriteNumber("inputSrid", srid);
            jsonWriter.WriteNumber("inputCount", inputCount);
            jsonWriter.WriteNumber("groupCount", dissolved.Count);

            jsonWriter.WritePropertyName("features");
            jsonWriter.WriteStartArray();
            foreach (var (key, geometry) in dissolved)
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("type", "Feature");

                jsonWriter.WritePropertyName("geometry");
                using (var doc = JsonDocument.Parse(writer.Write(geometry)))
                {
                    doc.RootElement.WriteTo(jsonWriter);
                }

                jsonWriter.WritePropertyName("properties");
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("processId", HandledProcessId);
                jsonWriter.WriteString("groupKey", key);
                jsonWriter.WriteNumber("inputSrid", srid);
                jsonWriter.WriteEndObject();

                jsonWriter.WriteEndObject();
            }
            jsonWriter.WriteEndArray();

            jsonWriter.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string BuildDataUri(byte[] payload)
    {
        var sb = new StringBuilder(payload.Length * 2 + FeatureCollectionDataUriPrefix.Length);
        sb.Append(FeatureCollectionDataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out DissolveInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkbs", out var wkbsRaw) || string.IsNullOrWhiteSpace(wkbsRaw))
        {
            error = "missing required input 'wkbs'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        if (!TryParseStringArray(wkbsRaw, "wkbs", out var wkbItems, out error))
        {
            return false;
        }

        if (wkbItems.Count == 0)
        {
            error = "input 'wkbs' must contain at least one geometry";
            return false;
        }

        List<string>? groupKeys = null;
        if (parameters.TryGetValue(prefix + "groupKeys", out var groupKeysRaw)
            && !string.IsNullOrWhiteSpace(groupKeysRaw))
        {
            if (!TryParseStringArray(groupKeysRaw, "groupKeys", out groupKeys, out error))
            {
                return false;
            }

            if (groupKeys.Count != wkbItems.Count)
            {
                error = $"input 'groupKeys' length ({groupKeys.Count}) must match 'wkbs' length ({wkbItems.Count})";
                return false;
            }
        }

        inputs = new DissolveInputs(wkbItems, groupKeys, srid);
        error = "";
        return true;
    }

    private static bool TryParseStringArray(
        string raw,
        string fieldName,
        out List<string> values,
        out string error)
    {
        values = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = $"input '{fieldName}' must be a JSON array";
                return false;
            }

            values.Capacity = doc.RootElement.GetArrayLength();
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = $"input '{fieldName}' element at index {index} is not a string";
                    return false;
                }

                var value = element.GetString();
                if (value == null)
                {
                    error = $"input '{fieldName}' element at index {index} is null";
                    return false;
                }

                // For wkbs we additionally require non-whitespace; for
                // groupKeys we accept the empty string as a legitimate key
                // (canonical "no attribute" bucket). The empty-base64 check
                // below catches degenerate WKB payloads regardless.
                if (string.Equals(fieldName, "wkbs", StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(value))
                {
                    error = $"input '{fieldName}' element at index {index} is empty";
                    return false;
                }

                values.Add(value);
                index++;
            }
        }
        catch (JsonException)
        {
            error = $"input '{fieldName}' is not valid JSON";
            return false;
        }

        error = "";
        return true;
    }

    private readonly record struct DissolveInputs(
        IReadOnlyList<string> WkbItems,
        IReadOnlyList<string>? GroupKeys,
        int Srid);

    private static partial class Log
    {
        [LoggerMessage(9200, LogLevel.Warning,
            "Geometry dissolve executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9201, LogLevel.Warning,
            "Geometry dissolve executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9202, LogLevel.Warning,
            "Geometry dissolve executor rejected job {OperationId}: WKB decode failed at index {Index}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, int index, string reason);

        [LoggerMessage(9203, LogLevel.Error,
            "Geometry dissolve executor failed job {OperationId} during dissolve computation for group {GroupKey}")]
        public static partial void DissolveComputationFailed(ILogger logger, string operationId, string groupKey, Exception exception);

        [LoggerMessage(9204, LogLevel.Warning,
            "Geometry dissolve executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
