// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.buffer-aggregate</c> layer-aware executor (#2322, #4623). The
/// job-executable counterpart of the layer-scoped PostGIS <c>SpatialAnalytics</c>
/// buffer-aggregate: it streams a Honua catalog layer through
/// <c>source.honua-layer</c>, buffers every feature by <c>distance</c> in the supplied
/// unit against the layer's ACTUAL resolved storage CRS (#4623 — never assumes CRS
/// units are meters), then optionally dissolves the buffers into one feature per
/// <c>groupByFields</c> group via <see cref="CascadedPolygonUnion"/>. Each emitted
/// feature carries a <c>COUNT</c> attribute.
/// </summary>
internal sealed class LayerBufferAggregateExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "analytics.buffer-aggregate";
    internal const string CountAttribute = "COUNT";

    public LayerBufferAggregateExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerBufferAggregateExecutor> logger,
        IOptions<LimitsOptions>? limitsOptions = null)
        : base(serviceScopeFactory, options, logger, limitsOptions)
    {
    }

    protected override string ProcessId => HandledProcessId;

    private protected override async Task<List<IFeature>> ApplyCoreAsync(
        LayerOpContext context,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var source = context.Features;
        var distance = ReadDistance(inputs);
        var distanceMeters = distance * ReadUnitFactor(inputs);
        var dissolve = ReadBool(inputs, "dissolve", defaultValue: true);
        var groupByFields = ReadGroupByFields(inputs);
        var stats = StatisticsSupport.ParseStatistics(inputs.GetOrDefault("outStatistics", string.Empty));
        if (stats.Count > 0 && !dissolve)
        {
            // Matches the catalog's documented constraint (and generalization.dissolve's
            // identical guard, #4624): per-feature output cannot carry aggregate columns.
            throw new TransformInputException(
                "'outStatistics' requires dissolve=true; per-feature output cannot carry aggregate columns.");
        }

        // #4629: without dissolve every buffered geometry is emitted as-is, so the running
        // vertex total of the buffers is already a lower bound on the artifact; charge it WHILE
        // buffering instead of discovering the overflow after every buffer was computed. With
        // dissolve the union can shrink the output, so the base's pre-serialization check
        // bounds the final result instead.
        long? maxOutputVertices = dissolve ? null : Options.CurrentValue.MaxArtifactBytes / MinSerializedBytesPerVertex;
        var bufferedGeometries = await BufferSourceFeaturesAsync(
                context, inputs, source, distanceMeters, maxOutputVertices, cancellationToken)
            .ConfigureAwait(false);

        var buffered = new List<(IFeature Feature, NtsGeometry Geometry, string GroupKey)>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bufferedGeometry = bufferedGeometries[i];
            if (bufferedGeometry is null || bufferedGeometry.IsEmpty)
            {
                continue;
            }

            buffered.Add((source[i], bufferedGeometry, BuildGroupKey(source[i], groupByFields)));
        }

        if (!dissolve)
        {
            var perFeature = new List<IFeature>(buffered.Count);
            foreach (var entry in buffered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = OverlayExecutorSupport.CopyAttributes(entry.Feature);
                OverlayExecutorSupport.Upsert(attributes, CountAttribute, 1L);
                perFeature.Add(new Feature(entry.Geometry, attributes));
            }

            return perFeature;
        }

        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in buffered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groups.TryGetValue(entry.GroupKey, out var accumulator))
            {
                accumulator = new GroupAccumulator(entry.Feature);
                groups[entry.GroupKey] = accumulator;
                order.Add(entry.GroupKey);
            }

            accumulator.Geometries.Add(entry.Geometry);
            StatisticsSupport.Accumulate(entry.Feature, stats, accumulator.Accumulators);
        }

        var dissolved = new List<IFeature>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accumulator = groups[key];
            var geometries = accumulator.Geometries;
            var unioned = geometries.Count == 1 ? geometries[0] : CascadedPolygonUnion.Union(geometries);

            var attributes = new AttributesTable();
            foreach (var field in groupByFields)
            {
                object? value = accumulator.First.Attributes is not null && accumulator.First.Attributes.Exists(field)
                    ? accumulator.First.Attributes.GetOptionalValue(field)
                    : null;
                OverlayExecutorSupport.Upsert(attributes, field, value);
            }

            OverlayExecutorSupport.Upsert(attributes, CountAttribute, (long)geometries.Count);
            foreach (var spec in stats)
            {
                object? value = spec.Kind == StatisticsSupport.StatKind.Count
                    ? (long)geometries.Count
                    : accumulator.Accumulators.TryGetValue(spec.Field, out var fieldAccumulator)
                        ? fieldAccumulator.Resolve(spec.Kind)
                        : null;
                OverlayExecutorSupport.Upsert(attributes, spec.OutputName, value);
            }

            dissolved.Add(new Feature(unioned, attributes));
        }

        return dissolved;
    }

    private sealed class GroupAccumulator
    {
        public GroupAccumulator(IFeature first) => First = first;

        public IFeature First { get; }

        public List<NtsGeometry> Geometries { get; } = [];

        public Dictionary<string, StatisticsSupport.FieldAccumulator> Accumulators { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Buffers every non-empty source geometry through the shared, CRS-aware
    /// <see cref="IGeometryOperationService"/> against the layer's ACTUAL resolved
    /// storage SRID (#4623). <see cref="IGeometryOperationService.BufferAsync"/> already
    /// carries the authoritative fix for both classes of bug this executor previously
    /// had: a geographic (degree) storage CRS is buffered through Web Mercator with the
    /// latitude-scale correction instead of treating the metric distance as degrees, and
    /// a projected storage CRS converts the metric distance into its true native linear
    /// unit (looked up from <c>spatial_ref_sys</c>) instead of assuming every projected
    /// CRS is metric. Returns a parallel array (one entry per <paramref name="source"/>
    /// feature, <see langword="null"/> for an empty/missing input geometry).
    /// </summary>
    private static async Task<NtsGeometry?[]> BufferSourceFeaturesAsync(
        LayerOpContext context,
        StepInputReader inputs,
        List<IFeature> source,
        double distanceMeters,
        long? maxOutputVertices,
        CancellationToken cancellationToken)
    {
        var results = new NtsGeometry?[source.Count];
        long outputVertices = 0;
        if (source.TrueForAll(f => f.Geometry is null || f.Geometry.IsEmpty))
        {
            return results;
        }

        var geometryOps = context.Services.GetService<IGeometryOperationService>();
        var metadataProvider = context.Services.GetService<IMetadataV2GraphProvider>();
        if (geometryOps is null || metadataProvider is null)
        {
            throw new TransformInputException(
                $"{HandledProcessId} requires the CRS-aware geometry service and catalog metadata " +
                "provider, which are not configured in this deployment.");
        }

        var layerId = RequireLayerId(inputs, "layerId");
        var snapshot = await metadataProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var srid = snapshot.ResolveStorageSrid(layerId)
            ?? throw new TransformInputException(
                $"layer {layerId}'s storage CRS could not be resolved; the layer may not exist or have no relational storage binding.");

        var wkbReader = new WKBReader();
        var wkbWriter = new WKBWriter();
        for (var i = 0; i < source.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = source[i].Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            byte[] bufferedWkb;
            try
            {
                bufferedWkb = await geometryOps.BufferAsync(
                        wkbWriter.Write(geometry), srid, distanceMeters, geodesic: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // BufferAsync rejects a planar buffer whose geometry crosses the Web Mercator
                // latitude limit (~85.0511 deg): a classified input failure, not a raw fault.
                throw new TransformInputException(ex.Message);
            }

            var buffered = wkbReader.Read(bufferedWkb);
            outputVertices += buffered.NumPoints;
            if (maxOutputVertices is { } budget && outputVertices > budget)
            {
                throw new TransformInputException(
                    $"the buffered output reached {outputVertices} vertices after {i + 1} of {source.Count} features, more than "
                    + "the configured MaxArtifactBytes can hold; stopped during buffering. Narrow the selection, set dissolve=true, "
                    + "or raise Geoprocessing:Executor:MaxArtifactBytes, then resubmit.");
            }

            results[i] = buffered;
        }

        return results;
    }

    // Unit-separator control char between key parts so distinct field boundaries never
    // collide (for example {"ab","c"} versus {"a","bc"}).
    private const char Separator = '';
    // NullMarker: U+001E (escape prefix) + U+0000 (NUL). Provably out-of-band in the
    // key encoding: EscapeKeyComponent emits U+001E only before U+001E or U+001F,
    // never before NUL, so no serialized attribute value can produce this sequence.
    private const string NullMarker = " ";

    private static string BuildGroupKey(IFeature feature, List<string> groupByFields)
    {
        if (groupByFields.Count == 0)
        {
            return string.Empty;
        }

        var attributes = feature.Attributes;
        var parts = new string[groupByFields.Count];
        for (var i = 0; i < groupByFields.Count; i++)
        {
            object? value = attributes is not null && attributes.Exists(groupByFields[i])
                ? attributes.GetOptionalValue(groupByFields[i])
                : null;
            parts[i] = value is null
                ? NullMarker
                : EscapeKeyComponent(Convert.ToString(value, CultureInfo.InvariantCulture) ?? NullMarker);
        }

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// Escapes U+001E and U+001F within a single group-key component value so they cannot be
    /// mistaken for structural characters in the composite key string. Encoding:
    /// <list type="bullet">
    ///   <item>U+001E in value -&gt; U+001E U+001E</item>
    ///   <item>U+001F in value -&gt; U+001E U+001F</item>
    /// </list>
    /// A single unescaped U+001F therefore always means "field boundary".
    /// </summary>
    private static string EscapeKeyComponent(string value)
    {
        if (value.IndexOfAny(['', '']) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch is '' or '')
            {
                sb.Append(''); // Escape prefix before any structural character.
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static double ReadDistance(StepInputReader inputs)
    {
        if (!inputs.TryGet("distance", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            throw new TransformInputException("'distance' must be a finite non-negative number");
        }

        return value;
    }

    private static double ReadUnitFactor(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("unit", "meters").Trim().ToLowerInvariant();
        return raw switch
        {
            "" or "meters" or "meter" or "m" => 1.0,
            "kilometers" or "kilometer" or "km" => 1000.0,
            "feet" or "foot" or "ft" => 0.3048,
            "miles" or "mile" or "mi" => 1609.344,
            _ => throw new TransformInputException(
                $"unit '{raw}' is not supported (allowed: meters, kilometers, feet, miles)"),
        };
    }

    private static bool ReadBool(StepInputReader inputs, string name, bool defaultValue)
    {
        if (!inputs.TryGet(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new TransformInputException($"'{name}' must be a boolean (true|false)"),
        };
    }

    private static List<string> ReadGroupByFields(StepInputReader inputs)
    {
        if (!inputs.TryGet("groupByFields", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
