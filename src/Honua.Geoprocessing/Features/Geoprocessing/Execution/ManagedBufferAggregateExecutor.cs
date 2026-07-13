// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.buffer-aggregate-managed</c> executor. A job-dispatchable,
/// managed (NetTopologySuite) buffer-and-dissolve counterpart to trunk's
/// <c>analytics.buffer-aggregate</c>, which runs only synchronously through the
/// layer-scoped PostGIS <c>SpatialAnalytics</c> protocol. Like
/// <see cref="ManagedSpatialJoinExecutor"/>, this is the
/// workflow/codemod-reachable counterpart that runs against an INLINE
/// FeatureCollection so the lean dispatcher can construct it unconditionally
/// without a Postgres dependency.
///
/// Buffers every input feature by <c>distance</c> in the supplied unit, then
/// optionally dissolves the result into one feature per <c>groupByFields</c>
/// group via <see cref="CascadedPolygonUnion"/>. Each emitted feature carries
/// a <c>COUNT</c> attribute holding the number of input geometries that
/// contributed to it. When <c>dissolve=false</c>, one buffered feature is
/// emitted per input with the input's attributes carried through verbatim.
///
/// Distance is normalised to CRS units after applying the unit factor
/// (meters/kilometers/feet/miles). Geodesic conversion is not performed —
/// callers operating on a geographic CRS must supply a distance already in
/// the layer's coordinate units. This matches the convention of the managed
/// <c>geometry.buffer</c> executor.
/// </summary>
internal sealed class ManagedBufferAggregateExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "analytics.buffer-aggregate-managed";
    internal const string CountAttribute = "COUNT";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var distance = ReadDistance(inputs);
        var unitFactor = ReadUnitFactor(inputs);
        var effectiveDistance = distance * unitFactor;

        // A linear/metric buffer distance is only meaningful against a projected (metric) CRS:
        // buffering geographic degrees by a metre distance silently produces wrong geometry.
        // When the caller declares the input CRS via 'sourceCrs' and it is geographic, reject a
        // non-zero linear buffer up front instead of returning a meters-as-degrees result (#2808).
        RejectLinearBufferOnGeographicCrs(inputs, distance);
        var dissolve = ReadBool(inputs, "dissolve", defaultValue: true);
        var groupByFields = ReadGroupByFields(inputs);

        var buffered = new List<BufferedInput>(source.Count);
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

            buffered.Add(new BufferedInput(feature, bufferedGeometry, BuildGroupKey(feature, groupByFields)));
        }

        if (!dissolve)
        {
            var output = new List<IFeature>(buffered.Count);
            foreach (var entry in buffered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Add(BuildPerFeature(entry));
            }

            return output;
        }

        var grouped = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var entry in buffered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!grouped.TryGetValue(entry.GroupKey, out var accumulator))
            {
                accumulator = new GroupAccumulator(entry.SourceFeature, groupByFields);
                grouped[entry.GroupKey] = accumulator;
                groupOrder.Add(entry.GroupKey);
            }

            accumulator.Add(entry.Geometry);
        }

        var dissolved = new List<IFeature>(groupOrder.Count);
        foreach (var key in groupOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dissolved.Add(grouped[key].Build());
        }

        return dissolved;
    }

    private static Feature BuildPerFeature(BufferedInput entry)
    {
        var merged = new AttributesTable();
        if (entry.SourceFeature.Attributes is not null)
        {
            foreach (var name in entry.SourceFeature.Attributes.GetNames())
            {
                if (string.Equals(name, CountAttribute, StringComparison.Ordinal))
                {
                    continue;
                }

                merged.Add(name, entry.SourceFeature.Attributes.GetOptionalValue(name));
            }
        }

        merged.Add(CountAttribute, 1L);
        return new Feature(entry.Geometry, merged);
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
                ? "\u0000"
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "\u0000";
        }

        return string.Join("\u001f", parts);
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

    /// <summary>
    /// Rejects a non-zero linear buffer when the caller declares (via <c>sourceCrs</c>) that
    /// the input geometries are in a geographic (lon/lat degree) CRS. The supported units
    /// (meters/kilometers/feet/miles) are all linear, so buffering degrees by any of them is a
    /// silent-wrong-answer; the caller must project to a metric CRS first. When <c>sourceCrs</c>
    /// is absent the behaviour is unchanged (coordinates are treated as CRS units).
    /// </summary>
    private static void RejectLinearBufferOnGeographicCrs(StepInputReader inputs, double distance)
    {
        if (distance <= 0d || !inputs.TryGet("sourceCrs", out var rawCrs) || string.IsNullOrWhiteSpace(rawCrs))
        {
            return;
        }

        if (!TryResolveSrid(rawCrs!, out var srid))
        {
            throw new TransformInputException(
                $"'sourceCrs' value '{rawCrs}' is not a recognized SRID or EPSG identifier");
        }

        if (Honua.Core.Features.Shared.Models.SpatialReference.Create(srid).IsGeographic)
        {
            throw new TransformInputException(
                $"'sourceCrs' EPSG:{srid} is geographic (degrees), but the buffer distance is linear (unit "
                + $"factor applied as CRS units); a meters-as-degrees buffer is meaningless. Reproject the input "
                + "to a projected/metric CRS before buffering, or supply the distance in the layer's coordinate units.");
        }
    }

    // Accepts a bare EPSG code ("4326"), an "EPSG:<code>" identifier, or the OGC CRS84 URIs;
    // mirrors the SRID resolution the metadata spatial-reference helper performs.
    private static bool TryResolveSrid(string value, out int srid)
    {
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
        {
            return true;
        }

        if (trimmed.Equals("CRS84", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("http://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            return true;
        }

        var separator = trimmed.LastIndexOfAny([':', '/']);
        var suffix = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
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
            _ => throw new TransformInputException(
                $"'{name}' must be a boolean (true|false)"),
        };
    }

    private static List<string> ReadGroupByFields(StepInputReader inputs)
    {
        if (!inputs.TryGet("groupByFields", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var fields = new List<string>();
        foreach (var token in raw!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            fields.Add(token);
        }

        return fields;
    }

    private readonly record struct BufferedInput(IFeature SourceFeature, NtsGeometry Geometry, string GroupKey);

    private sealed class GroupAccumulator
    {
        private readonly IFeature _firstFeature;
        private readonly List<string> _groupByFields;
        private readonly List<NtsGeometry> _geometries = new();

        public GroupAccumulator(IFeature firstFeature, List<string> groupByFields)
        {
            _firstFeature = firstFeature;
            _groupByFields = groupByFields;
        }

        public void Add(NtsGeometry geometry) => _geometries.Add(geometry);

        public Feature Build()
        {
            var unioned = _geometries.Count == 1
                ? _geometries[0]
                : CascadedPolygonUnion.Union(_geometries);

            var attributes = new AttributesTable();
            if (_groupByFields.Count > 0 && _firstFeature.Attributes is not null)
            {
                foreach (var field in _groupByFields)
                {
                    object? value = _firstFeature.Attributes.Exists(field)
                        ? _firstFeature.Attributes.GetOptionalValue(field)
                        : null;
                    attributes.Add(field, value);
                }
            }

            attributes.Add(CountAttribute, (long)_geometries.Count);
            return new Feature(unioned, attributes);
        }
    }
}
