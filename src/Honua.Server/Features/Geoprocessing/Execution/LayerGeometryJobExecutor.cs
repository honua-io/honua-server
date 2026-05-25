// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the layer-scope (feature-collection)
/// vector geoprocessing processes. Where the <c>Geometry*JobExecutor</c> family
/// operates on a single base64-WKB geometry, this executor takes an INPUT LAYER
/// reference, streams its features, applies the geometry operation across the
/// collection (carrying attributes through), and emits an OUTPUT LAYER / artifact.
///
/// A GP process over a layer == a single-transform ETL pipeline, so this reuses
/// the GeoETL machinery already on this branch: the streaming feature model
/// (<see cref="IFeature"/> over NetTopologySuite), the managed per-feature
/// <see cref="GeometryOperationTransform"/> / <see cref="ReprojectTransform"/>,
/// and the inline-GeoJSON <see cref="ILayerFeatureSource"/>. The single-WKB
/// path stays as a primitive but is no longer the only path.
///
/// Input contract (projected onto the canonical step-input bag):
/// <list type="bullet">
/// <item><c>inputGeoJson</c> — an inline GeoJSON FeatureCollection (managed path,
/// always available).</item>
/// <item><c>layerId</c> — a catalog layer id (resolved when a storage-provider
/// <see cref="ILayerFeatureSource"/> that can read catalog layers is registered).</item>
/// </list>
/// Output: a GeoJSON FeatureCollection artifact on the <c>outputFeatureLayer</c>
/// slot. (Persisting to a durable Honua layer reuses
/// <c>HonuaLayerSinkConnector</c>; that path is wired when a target layer name and
/// an <c>IFeatureSinkWriter</c> are supplied — see the roadmap note.)
///
/// Pure managed NetTopologySuite — no GDAL/GEOS. Raster / surface families are
/// routed to the native GDAL worker and are not handled here.
/// </summary>
internal sealed partial class LayerGeometryJobExecutor : IJobExecutor
{
    /// <summary>Process ids this executor handles at layer scope.</summary>
    internal static readonly FrozenSet<string> HandledProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "generalization.simplify-layer",
            "conversion.feature-project",
            "geometry.make-valid",
            "geometry.difference",
            "generalization.dissolve",
            "analytics.spatial-join",
        }.ToFrozenSet(StringComparer.Ordinal);

    private const string DataUriPrefix = "data:application/geo+json;base64,";

    private readonly IEnumerable<ILayerFeatureSource> _sources;
    private readonly GeometryOperationTransform _geometryOp;
    private readonly ReprojectTransform _reproject;
    private readonly DissolveTransform _dissolve;
    private readonly SpatialJoinTransform _spatialJoin;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<LayerGeometryJobExecutor> _logger;

    public LayerGeometryJobExecutor(
        IEnumerable<ILayerFeatureSource> sources,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerGeometryJobExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = sources;
        _geometryOp = new GeometryOperationTransform();
        _reproject = new ReprojectTransform();
        _dissolve = new DissolveTransform();
        _spatialJoin = new SpatialJoinTransform();
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
        if (string.IsNullOrWhiteSpace(processId) || !HandledProcessIds.Contains(processId))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the layer-scope geometry executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing layer inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadReference(parameters, out var reference, out var refError))
        {
            Log.InvalidInputs(_logger, job.OperationId, refError);
            return JobExecutionResult.Failed($"Invalid layer inputs: {refError}");
        }

        var source = _sources.FirstOrDefault(s => s.CanResolve(reference.Kind));
        if (source is null)
        {
            return JobExecutionResult.Failed(
                $"No layer feature source is registered for reference kind '{reference.Kind}'. " +
                "The inline-GeoJSON path is always available; catalog-layer ids require a storage-provider source.");
        }

        if (!TryBuildTransform(processId!, parameters, out var transform, out var transformConfig, out var opError))
        {
            Log.InvalidInputs(_logger, job.OperationId, opError);
            return JobExecutionResult.Failed($"Invalid layer inputs: {opError}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Applying geometry operation across features", cancellationToken)
            .ConfigureAwait(false);

        var stream = transform.TransformAsync(transformConfig, source.ReadAsync(reference, cancellationToken), cancellationToken);

        byte[] payload;
        long featureCount;
        try
        {
            (payload, featureCount) = await WriteFeatureCollectionAsync(stream, processId!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LayerComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Layer geometry operation failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (featureCount == 0)
        {
            return JobExecutionResult.Failed(
                "Layer geometry operation produced no output features; verify the input layer and parameters.");
        }

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Layer artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Pre-filter the input layer or persist to a durable layer sink instead.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding output layer artifact", cancellationToken).ConfigureAwait(false);

        var artifactUri = BuildDataUri(payload);
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Layer operation completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private bool TryBuildTransform(
        string processId,
        IReadOnlyDictionary<string, string> parameters,
        out Core.Features.GeoETL.Abstractions.IPipelineTransform transform,
        out TransformConfig config,
        out string error)
    {
        transform = _geometryOp;
        config = null!;
        error = "";
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        switch (processId)
        {
            case "generalization.simplify-layer":
            {
                if (!TryReadDouble(parameters, prefix + "tolerance", requirePositive: true, out var tolerance, out error))
                {
                    return false;
                }

                var preserve = ReadBool(parameters, prefix + "preserveTopology", defaultValue: true);
                transform = _geometryOp;
                config = new TransformConfig
                {
                    Type = GeometryOperationTransform.TransformType,
                    Options = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["op"] = "simplify",
                        ["tolerance"] = tolerance.ToString(CultureInfo.InvariantCulture),
                        ["preserveTopology"] = preserve ? "true" : "false",
                    }
                };
                return true;
            }

            case "conversion.feature-project":
            {
                if (!TryReadInt(parameters, prefix + "targetSrid", out var toSrid, out error))
                {
                    error = "missing or invalid input 'targetSrid'; expected a positive integer";
                    return false;
                }

                // fromSrid is optional; default to WGS 84 when the input layer's CRS
                // is not supplied. The managed reproject transform validates the pair.
                var fromSrid = TryReadInt(parameters, prefix + "fromSrid", out var parsedFrom, out _) ? parsedFrom : 4326;
                transform = _reproject;
                config = new TransformConfig
                {
                    Type = ReprojectTransform.TransformType,
                    Options = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fromSrid"] = fromSrid.ToString(CultureInfo.InvariantCulture),
                        ["toSrid"] = toSrid.ToString(CultureInfo.InvariantCulture),
                    }
                };
                return true;
            }

            case "geometry.make-valid":
            {
                transform = _geometryOp;
                config = new TransformConfig
                {
                    Type = GeometryOperationTransform.TransformType,
                    Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "make-valid" }
                };
                return true;
            }

            case "generalization.dissolve":
            {
                // groupByFields is OPTIONAL: when omitted the transform collapses
                // every feature into a single group ("dissolve all"), matching
                // ArcGIS Dissolve_management with no dissolve field.
                var options = new Dictionary<string, string>(StringComparer.Ordinal);
                if (parameters.TryGetValue(prefix + "groupByFields", out var groupByFields)
                    && !string.IsNullOrWhiteSpace(groupByFields))
                {
                    options["groupByFields"] = groupByFields;
                }

                // statistics is the semicolon-separated field:stat summary spec
                // (e.g. "pop:sum;area:mean;:count"). Optional.
                if (parameters.TryGetValue(prefix + "statistics", out var statistics)
                    && !string.IsNullOrWhiteSpace(statistics))
                {
                    options["statistics"] = statistics;
                }

                transform = _dissolve;
                config = new TransformConfig
                {
                    Type = DissolveTransform.TransformType,
                    Options = options
                };
                return true;
            }

            case "analytics.spatial-join":
            {
                // The TARGET layer flows through the standard input reference
                // (inputGeoJson / layerId). The JOIN layer is supplied inline as
                // 'joinGeoJson' so the managed inline path can carry both layers
                // without a storage provider.
                if (!parameters.TryGetValue(prefix + "joinGeoJson", out var joinGeoJson)
                    || string.IsNullOrWhiteSpace(joinGeoJson))
                {
                    error = "missing required input 'joinGeoJson' (the join layer as an inline GeoJSON FeatureCollection) for spatial-join";
                    return false;
                }

                var options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["referenceInline"] = joinGeoJson,
                    // Summarizing (one-to-one) form: aggregate matched join rows.
                    ["aggregate"] = "true",
                };

                if (parameters.TryGetValue(prefix + "predicate", out var predicate)
                    && !string.IsNullOrWhiteSpace(predicate))
                {
                    options["predicate"] = predicate;
                }

                // statistics is the semicolon-separated field:stat summary spec
                // (e.g. "value:sum;value:mean;:count"). Optional — JOIN_COUNT is
                // always emitted regardless.
                if (parameters.TryGetValue(prefix + "statistics", out var statistics)
                    && !string.IsNullOrWhiteSpace(statistics))
                {
                    options["statistics"] = statistics;
                }

                transform = _spatialJoin;
                config = new TransformConfig
                {
                    Type = SpatialJoinTransform.TransformType,
                    Options = options
                };
                return true;
            }

            case "geometry.difference":
            {
                if (!parameters.TryGetValue(prefix + "regionWkt", out var wkt) || string.IsNullOrWhiteSpace(wkt))
                {
                    error = "missing required input 'regionWkt' (the eraser geometry as WKT) for layer-scope difference";
                    return false;
                }

                transform = _geometryOp;
                config = new TransformConfig
                {
                    Type = GeometryOperationTransform.TransformType,
                    Options = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["op"] = "difference",
                        ["regionWkt"] = wkt,
                    }
                };
                return true;
            }

            default:
                error = $"process '{processId}' has no layer-scope transform mapping";
                return false;
        }
    }

    private static bool TryReadReference(
        IReadOnlyDictionary<string, string> parameters,
        out LayerFeatureReference reference,
        out string error)
    {
        reference = null!;
        error = "";
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (parameters.TryGetValue(prefix + "inputGeoJson", out var inline) && !string.IsNullOrWhiteSpace(inline))
        {
            reference = new LayerFeatureReference
            {
                Kind = LayerFeatureSourceKind.InlineGeoJson,
                InlineGeoJson = inline
            };
            return true;
        }

        if (parameters.TryGetValue(prefix + "layerId", out var layerId) && !string.IsNullOrWhiteSpace(layerId))
        {
            parameters.TryGetValue(prefix + "where", out var where);
            reference = new LayerFeatureReference
            {
                Kind = LayerFeatureSourceKind.CatalogLayer,
                LayerId = layerId,
                Where = string.IsNullOrWhiteSpace(where) ? null : where
            };
            return true;
        }

        error = "missing input layer reference; supply either 'inputGeoJson' (inline FeatureCollection) or 'layerId'";
        return false;
    }

    private static async Task<(byte[] Payload, long Count)> WriteFeatureCollectionAsync(
        IAsyncEnumerable<IFeature> features,
        string processId,
        CancellationToken cancellationToken)
    {
        var geoJsonWriter = new GeoJsonWriter();
        long count = 0;

        using var buffer = new MemoryStream();
        await using (var textWriter = new StreamWriter(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        using (var jsonWriter = new Newtonsoft.Json.JsonTextWriter(textWriter))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WritePropertyName("type");
            jsonWriter.WriteValue("FeatureCollection");
            jsonWriter.WritePropertyName("processId");
            jsonWriter.WriteValue(processId);
            jsonWriter.WritePropertyName("features");
            jsonWriter.WriteStartArray();

            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var concrete = feature as Feature ?? new Feature(feature.Geometry, feature.Attributes);
                geoJsonWriter.Write(concrete, jsonWriter);
                count++;
            }

            jsonWriter.WriteEndArray();
            jsonWriter.WritePropertyName("featureCount");
            jsonWriter.WriteValue(count);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
        }

        return (buffer.ToArray(), count);
    }

    private static string BuildDataUri(byte[] payload)
    {
        var sb = new StringBuilder(payload.Length * 2 + DataUriPrefix.Length);
        sb.Append(DataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out int value,
        out string error)
    {
        error = "";
        if (parameters.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0)
        {
            return true;
        }

        value = 0;
        error = $"missing or invalid '{key}'";
        return false;
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        bool requirePositive,
        out double value,
        out string error)
    {
        error = "";
        if (parameters.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value)
            && (!requirePositive || value > 0))
        {
            return true;
        }

        value = 0;
        error = requirePositive
            ? $"missing or invalid '{key}'; expected a positive finite number"
            : $"missing or invalid '{key}'; expected a finite number";
        return false;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        bool defaultValue)
    {
        if (parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static partial class Log
    {
        [LoggerMessage(9200, LogLevel.Warning,
            "Layer geometry executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9201, LogLevel.Warning,
            "Layer geometry executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9202, LogLevel.Error,
            "Layer geometry executor failed job {OperationId} during the layer operation")]
        public static partial void LayerComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9203, LogLevel.Warning,
            "Layer geometry executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
