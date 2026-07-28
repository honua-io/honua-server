// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NtsEnvelope = NetTopologySuite.Geometries.Envelope;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared managed (NetTopologySuite) spatial-join computation used by the
/// layer-aware <c>analytics.spatial-join</c> executor (#2322) and the
/// <c>enrichment.enrich</c> job executor (#2283). For each target feature it
/// summarizes every join feature satisfying a spatial predicate into a
/// <c>JOIN_COUNT</c> plus carried-attribute arrays and numeric aggregates,
/// preserving zero-match targets one-to-one. Predicates use NTS's managed
/// relational operators; distances are evaluated in the CRS units of the supplied
/// geometries — geodesic conversion is not performed, matching the other managed
/// tool packs. Candidate join features are pruned through an in-memory
/// <see cref="STRtree{T}"/> index before the exact predicate test.
/// </summary>
internal static class SpatialJoinSupport
{
    /// <summary>Attribute holding the count of matched join features on each target.</summary>
    internal const string JoinCountAttribute = "JOIN_COUNT";

    /// <summary>Spatial predicate evaluated between a join candidate and a target feature.</summary>
    internal enum SpatialPredicate
    {
        /// <summary>The join geometry intersects the target.</summary>
        Intersects,

        /// <summary>The join geometry contains the target — the classic point-in-polygon case.</summary>
        Contains,

        /// <summary>The target contains the join geometry.</summary>
        Within,

        /// <summary>The join geometry lies within a distance threshold of the target (CRS units).</summary>
        Dwithin,
    }

    /// <summary>
    /// Builds an in-memory STRtree over the join features' envelopes, skipping
    /// null/empty geometries.
    /// </summary>
    internal static STRtree<IFeature> BuildIndex(
        IReadOnlyList<IFeature> joinFeatures,
        CancellationToken cancellationToken)
    {
        var index = new STRtree<IFeature>();
        foreach (var feature in joinFeatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is not null && !geometry.IsEmpty)
            {
                index.Insert(geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        return index;
    }

    /// <summary>
    /// Joins a single target feature against the indexed join set: counts matches
    /// into <see cref="JoinCountAttribute"/>, emits each <paramref name="carryFields"/>
    /// column as an array of the matched join values (empty array on zero matches),
    /// and computes the <paramref name="stats"/> aggregates over the matched join
    /// rows through the shared <see cref="StatisticsSupport"/> accumulators.
    /// </summary>
    internal static Feature Join(
        IFeature target,
        STRtree<IFeature> index,
        SpatialPredicate predicate,
        double distance,
        IReadOnlyList<string> carryFields,
        IReadOnlyList<StatisticsSupport.StatSpec> stats)
    {
        var attributes = OverlayExecutorSupport.CopyAttributes(target);

        var carried = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var field in carryFields)
        {
            carried[field] = [];
        }

        var accumulators = new Dictionary<string, StatisticsSupport.FieldAccumulator>(StringComparer.Ordinal);
        foreach (var spec in stats.Where(spec => spec.Kind != StatisticsSupport.StatKind.Count && !accumulators.ContainsKey(spec.Field)))
        {
            accumulators[spec.Field] = new StatisticsSupport.FieldAccumulator();
        }

        long matchCount = 0;
        var geometry = target.Geometry;
        if (geometry is not null && !geometry.IsEmpty)
        {
            foreach (var candidate in index.Query(QueryEnvelope(geometry, predicate, distance)))
            {
                if (!Matches(candidate.Geometry, geometry, predicate, distance))
                {
                    continue;
                }

                matchCount++;
                foreach (var field in carryFields)
                {
                    carried[field].Add(ReadValue(candidate, field));
                }

                foreach (var accumulator in accumulators)
                {
                    switch (StatisticsSupport.TryReadNumeric(candidate, accumulator.Key, out var value))
                    {
                        case true:
                            accumulator.Value.Add(value);
                            break;
                    }
                }
            }
        }

        OverlayExecutorSupport.Upsert(attributes, JoinCountAttribute, matchCount);
        foreach (var field in carryFields)
        {
            OverlayExecutorSupport.Upsert(attributes, field, carried[field].ToArray());
        }

        foreach (var spec in stats)
        {
            object? value = spec.Kind == StatisticsSupport.StatKind.Count
                ? matchCount
                : accumulators.TryGetValue(spec.Field, out var accumulator)
                    ? accumulator.Resolve(spec.Kind)
                    : null;
            OverlayExecutorSupport.Upsert(attributes, spec.OutputName, value);
        }

        return new Feature(target.Geometry, attributes);
    }

    /// <summary>Reads a join feature's attribute value, or null when absent.</summary>
    internal static object? ReadValue(IFeature feature, string field)
        => feature.Attributes is not null && feature.Attributes.Exists(field)
            ? feature.Attributes.GetOptionalValue(field)
            : null;

    private static NtsEnvelope QueryEnvelope(NtsGeometry targetGeometry, SpatialPredicate predicate, double distance)
    {
        var envelope = targetGeometry.EnvelopeInternal.Copy();
        if (predicate == SpatialPredicate.Dwithin)
        {
            // Widen the candidate window by the distance threshold so join geometries
            // whose envelopes fall just outside the target's are still tested exactly.
            envelope.ExpandBy(distance);
        }

        return envelope;
    }

    private static bool Matches(
        NtsGeometry? joinGeometry,
        NtsGeometry targetGeometry,
        SpatialPredicate predicate,
        double distance)
    {
        if (joinGeometry is null || joinGeometry.IsEmpty)
        {
            return false;
        }

        return predicate switch
        {
            SpatialPredicate.Contains => joinGeometry.Contains(targetGeometry),
            SpatialPredicate.Within => targetGeometry.Contains(joinGeometry),
            SpatialPredicate.Dwithin => joinGeometry.IsWithinDistance(targetGeometry, distance),
            _ => joinGeometry.Intersects(targetGeometry),
        };
    }
}
