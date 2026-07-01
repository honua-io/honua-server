// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.buffer-aggregate</c> layer-aware executor (#2322). The job-executable
/// counterpart of the layer-scoped PostGIS <c>SpatialAnalytics</c> buffer-aggregate:
/// it streams a Honua catalog layer through <c>source.honua-layer</c>, buffers every
/// feature by <c>distance</c> in the supplied unit, then optionally dissolves the
/// buffers into one feature per <c>groupByFields</c> group via
/// <see cref="CascadedPolygonUnion"/>. Each emitted feature carries a <c>COUNT</c>
/// attribute. Distance is normalised to CRS units after applying the unit factor
/// (meters/kilometers/feet/miles); geodesic conversion is not performed — this
/// matches the managed <c>analytics.buffer-aggregate-managed</c> convention.
/// </summary>
internal sealed class LayerBufferAggregateExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "analytics.buffer-aggregate";
    internal const string CountAttribute = "COUNT";

    public LayerBufferAggregateExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerBufferAggregateExecutor> logger)
        : base(serviceScopeFactory, options, logger)
    {
    }

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        List<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var distance = ReadDistance(inputs);
        var effectiveDistance = distance * ReadUnitFactor(inputs);
        var dissolve = ReadBool(inputs, "dissolve", defaultValue: true);
        var groupByFields = ReadGroupByFields(inputs);

        var buffered = new List<(IFeature Feature, NtsGeometry Geometry, string GroupKey)>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var bufferedGeometry = geometry.Buffer(effectiveDistance);
            if (bufferedGeometry is null || bufferedGeometry.IsEmpty)
            {
                continue;
            }

            buffered.Add((feature, bufferedGeometry, BuildGroupKey(feature, groupByFields)));
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

        var groups = new Dictionary<string, (IFeature First, List<NtsGeometry> Geometries)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in buffered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groups.TryGetValue(entry.GroupKey, out var accumulator))
            {
                accumulator = (entry.Feature, new List<NtsGeometry>());
                groups[entry.GroupKey] = accumulator;
                order.Add(entry.GroupKey);
            }

            accumulator.Geometries.Add(entry.Geometry);
        }

        var dissolved = new List<IFeature>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (first, geometries) = groups[key];
            var unioned = geometries.Count == 1 ? geometries[0] : CascadedPolygonUnion.Union(geometries);

            var attributes = new AttributesTable();
            foreach (var field in groupByFields)
            {
                object? value = first.Attributes is not null && first.Attributes.Exists(field)
                    ? first.Attributes.GetOptionalValue(field)
                    : null;
                OverlayExecutorSupport.Upsert(attributes, field, value);
            }

            OverlayExecutorSupport.Upsert(attributes, CountAttribute, (long)geometries.Count);
            dissolved.Add(new Feature(unioned, attributes));
        }

        return dissolved;
    }

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
                ? ""
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        return string.Join("", parts);
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
