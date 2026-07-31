// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SpatialAnalytics.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
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
/// <para>
/// Predicates are the canonical <see cref="SpatialJoinPredicate"/> members shared with
/// the PostGIS pushdown (honua-server#3069), whose names spell out both operands, so a
/// managed join cannot drift from the SQL one. Each caller owns the mapping from its own
/// wire vocabulary onto them.
/// </para>
/// </summary>
internal static class SpatialJoinSupport
{
    /// <summary>Attribute holding the count of matched join features on each target.</summary>
    internal const string JoinCountAttribute = "JOIN_COUNT";

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
        SpatialJoinPredicate predicate,
        double distance,
        IReadOnlyList<string> carryFields,
        IReadOnlyList<StatisticsSupport.StatSpec> stats,
        MatchBudget? budget = null)
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

                // Carried values are what actually grow without bound: two highly
                // overlapping layers buffer targets x matches x fields values before the
                // artifact-size check ever runs. Charge each one against the shared
                // budget so the job fails fast instead of exhausting the worker.
                budget?.Charge(carryFields.Count);

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

    /// <summary>
    /// <see cref="IItemDistance{Envelope, IFeature}"/> over indexed features, so an
    /// <see cref="STRtree{T}"/> can answer exact nearest-neighbour queries on the
    /// features' geometries (planar CRS-unit distance) instead of the callers
    /// scanning every candidate.
    /// </summary>
    internal sealed class FeatureDistance : IItemDistance<NtsEnvelope, IFeature>
    {
        /// <summary>Shared stateless instance.</summary>
        internal static readonly FeatureDistance Instance = new();

        /// <inheritdoc />
        public double Distance(IBoundable<NtsEnvelope, IFeature> item1, IBoundable<NtsEnvelope, IFeature> item2)
        {
            var first = item1.Item?.Geometry;
            var second = item2.Item?.Geometry;
            if (first is null || first.IsEmpty || second is null || second.IsEmpty)
            {
                return double.PositiveInfinity;
            }

            return first.Distance(second);
        }
    }

    /// <summary>
    /// Cumulative budget over the carried match values a whole join may buffer. The
    /// per-layer admission caps bound each INPUT, but the join itself is a Cartesian
    /// product: highly overlapping layers can materialize far more carried values than
    /// either input has features. Charging every carried value against one shared budget
    /// makes that growth fail fast with an actionable message.
    /// </summary>
    internal sealed class MatchBudget
    {
        private readonly long _limit;
        private long _used;

        /// <summary>Creates a budget over the maximum number of carried match values.</summary>
        /// <param name="limit">Maximum carried values across the entire join.</param>
        internal MatchBudget(long limit) => _limit = limit;

        /// <summary>
        /// Charges <paramref name="count"/> carried values, throwing once the cumulative
        /// budget is exhausted.
        /// </summary>
        /// <param name="count">Number of carried values produced by one match.</param>
        internal void Charge(int count)
        {
            if (count <= 0)
            {
                return;
            }

            _used += count;
            if (_used > _limit)
            {
                throw new TransformInputException(
                    $"the join exceeded the cumulative match budget of {_limit} carried values; "
                    + "narrow the selection (where/bbox), carry fewer 'outputFields', or use a less "
                    + "permissive spatial method.");
            }
        }
    }

    /// <summary>Reads a join feature's attribute value, or null when absent.</summary>
    internal static object? ReadValue(IFeature feature, string field)
        => feature.Attributes is not null && feature.Attributes.Exists(field)
            ? feature.Attributes.GetOptionalValue(field)
            : null;

    private static NtsEnvelope QueryEnvelope(NtsGeometry targetGeometry, SpatialJoinPredicate predicate, double distance)
    {
        var envelope = targetGeometry.EnvelopeInternal.Copy();
        if (predicate == SpatialJoinPredicate.DWithin)
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
        SpatialJoinPredicate predicate,
        double distance)
    {
        if (joinGeometry is null || joinGeometry.IsEmpty)
        {
            return false;
        }

        return predicate switch
        {
            SpatialJoinPredicate.JoinContainsTarget => joinGeometry.Contains(targetGeometry),
            SpatialJoinPredicate.TargetContainsJoin => targetGeometry.Contains(joinGeometry),
            SpatialJoinPredicate.DWithin => joinGeometry.IsWithinDistance(targetGeometry, distance),
            _ => joinGeometry.Intersects(targetGeometry),
        };
    }
}
