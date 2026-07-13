// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;
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
/// Distance is normalised to planar CRS units after applying the unit factor
/// (meters/kilometers/feet/miles). Geodesic conversion is not performed, so the
/// linear unit is only meaningful on a metric projected CRS. A linear-unit buffer
/// against a geographic (lon/lat degree) CRS — resolved from the explicit <c>srid</c>
/// input, else the input geometry SRID, else the GeoJSON default 4326 — is rejected
/// via the canonical <see cref="GeographicSridClassifier"/> rather than silently
/// applying metres as degrees. Callers must reproject to a metric CRS (or declare the
/// projected <c>srid</c> the geometries are in) first. This matches the convention of
/// the managed <c>geometry.buffer</c> executor.
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
        var (unitName, unitFactor) = ReadUnit(inputs);
        var srid = ResolveSrid(inputs, source);

        // A linear unit (metres/km/feet/miles) applied as planar CRS units to a geographic
        // (lon/lat degree) CRS silently produces wrong-scale geometry — a "500 metres"
        // buffer becomes a 500-degree buffer. Reject up front instead of returning a
        // plausible-but-wrong result; the caller must reproject to a metric/projected CRS
        // first (e.g. transform.reproject to EPSG:3857) or declare the projected 'srid'
        // the geometries are actually in. Uses the canonical SRID classifier (#2732), not
        // a local allowlist.
        if (GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid))
        {
            throw new TransformInputException(string.Format(
                CultureInfo.InvariantCulture,
                "buffer distance unit '{0}' is a linear (metric) unit but the input CRS (EPSG:{1}) is geographic "
                + "(lon/lat degrees); a metric buffer applied to degree coordinates produces wrong-scale geometry. "
                + "Reproject the features to a metric/projected CRS (e.g. transform.reproject to EPSG:3857) before "
                + "buffering, or set 'srid' to the projected CRS the geometries are actually in.",
                unitName, srid));
        }

        var effectiveDistance = distance * unitFactor;
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

    private static (string Name, double Factor) ReadUnit(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("unit", "meters").Trim().ToLowerInvariant();
        var factor = raw switch
        {
            "" or "meters" or "meter" or "m" => 1.0,
            "kilometers" or "kilometer" or "km" => 1000.0,
            "feet" or "foot" or "ft" => 0.3048,
            "miles" or "mile" or "mi" => 1609.344,
            _ => throw new TransformInputException(
                $"unit '{raw}' is not supported (allowed: meters, kilometers, feet, miles)"),
        };

        return (string.IsNullOrEmpty(raw) ? "meters" : raw, factor);
    }

    /// <summary>
    /// Resolves the CRS the buffer distance is applied in. Prefers an explicit
    /// <c>srid</c> input; otherwise samples the first non-empty input geometry's SRID
    /// (preserved end-to-end across streamed artifacts); otherwise defaults to WGS&#160;84
    /// (4326), the GeoJSON convention. The resolved SRID drives the geographic-unit guard
    /// so a metric buffer on degree coordinates is rejected rather than silently wrong.
    /// </summary>
    private static int ResolveSrid(StepInputReader inputs, FeatureCollection source)
    {
        if (inputs.TryGet("srid", out var raw)
            && !string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared)
                || declared <= 0)
            {
                throw new TransformInputException("'srid' must be a positive integer SRID/WKID");
            }

            return declared;
        }

        foreach (var feature in source)
        {
            var geometry = feature.Geometry;
            if (geometry is not null && !geometry.IsEmpty && geometry.SRID > 0)
            {
                return geometry.SRID;
            }
        }

        return 4326;
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
