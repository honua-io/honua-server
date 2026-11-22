// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.SpatialAnalytics.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Distance;
using NtsEnvelope = NetTopologySuite.Geometries.Envelope;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

internal static class SpatialJoinSupport
{
    internal const string JoinCountAttribute = "JOIN_COUNT";

    internal const long DefaultMaxCarriedMatchValues = 20_000_000L;

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

    internal static Feature Join(
        IFeature target,
        STRtree<IFeature> index,
        SpatialJoinPredicate predicate,
        double distance,
        IReadOnlyList<string> carryFields,
        IReadOnlyList<StatisticsSupport.StatSpec> stats,
        MatchBudget? budget = null,
        int? srid = null,
        CancellationToken cancellationToken = default)
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
        var effectiveSrid = srid ?? (geometry?.SRID > 0 ? geometry.SRID : 0);
        var isGeographic = effectiveSrid > 0 && GeodesicGeometryOperations.IsGeographicSrid(effectiveSrid);
        if (geometry is not null && !geometry.IsEmpty)
        {
            foreach (var candidate in index.Query(QueryEnvelope(geometry, predicate, distance, isGeographic)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget?.ChargeCandidate();

                if (!Matches(candidate.Geometry, geometry, predicate, distance, isGeographic))
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

                StatisticsSupport.Accumulate(candidate, stats, accumulators);
            }
        }

        // Carried attributes FIRST, computed fields after. A dataset whose attributes include
        // JOIN_COUNT (or a caller naming it in outputFields) otherwise overwrote the computed
        // count with the carried array, so the result silently violated the contract every
        // consumer reads. The computed field is the one this process
        // promises, so it wins the collision.
        foreach (var field in carryFields)
        {
            OverlayExecutorSupport.Upsert(attributes, field, carried[field].ToArray());
        }

        OverlayExecutorSupport.Upsert(attributes, JoinCountAttribute, matchCount);

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

    internal sealed class FeatureDistance : IItemDistance<NtsEnvelope, IFeature>
    {
        internal static readonly FeatureDistance Instance = new();

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

    // Candidate evaluations and carried values have separate ceilings because rejected pairs burn CPU without growing the carried-value count.
    internal sealed class MatchBudget
    {
        internal const long DefaultCandidateEvaluations = 50_000_000L;

        private readonly long _limit;
        private readonly long _candidateLimit;
        private long _used;
        private long _candidatesEvaluated;

        // The candidate ceiling does not derive from the carried-value ceiling, so lowering one does not crush a join that was not pathological.
        internal MatchBudget(long limit, long? candidateLimit = null)
        {
            _limit = limit;
            _candidateLimit = candidateLimit ?? DefaultCandidateEvaluations;
        }

        internal void ChargeCandidate()
        {
            _candidatesEvaluated++;
            if (_candidatesEvaluated > _candidateLimit)
            {
                throw new TransformInputException(
                    $"the join exceeded the cumulative candidate budget of {_candidateLimit} "
                    + "spatial comparisons; the inputs' envelopes overlap too broadly for this "
                    + "method. Narrow the selection (where/bbox) or use a more selective spatial "
                    + "method.");
            }
        }

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

    internal static object? ReadValue(IFeature feature, string field)
        => feature.Attributes is not null && feature.Attributes.Exists(field)
            ? feature.Attributes.GetOptionalValue(field)
            : null;

    private static NtsEnvelope QueryEnvelope(
        NtsGeometry targetGeometry,
        SpatialJoinPredicate predicate,
        double distance,
        bool isGeographic)
    {
        var envelope = targetGeometry.EnvelopeInternal.Copy();
        if (predicate == SpatialJoinPredicate.DWithin)
        {
            if (isGeographic)
            {
                // Convert meter distance to conservative latitude/longitude degree margins.
                var latDeg = distance / 110_574.0;
                var maxAbsLat = Math.Min(89.9, Math.Max(Math.Abs(envelope.MinY), Math.Abs(envelope.MaxY)) + latDeg);
                var lonDeg = maxAbsLat >= 89.5
                    ? 180.0
                    : distance / (111_320.0 * Math.Cos(maxAbsLat * Math.PI / 180.0));
                envelope.ExpandBy(lonDeg, latDeg);
            }
            else
            {
                envelope.ExpandBy(distance);
            }
        }

        return envelope;
    }

    private static bool Matches(
        NtsGeometry? joinGeometry,
        NtsGeometry targetGeometry,
        SpatialJoinPredicate predicate,
        double distance,
        bool isGeographic)
    {
        if (joinGeometry is null || joinGeometry.IsEmpty)
        {
            return false;
        }

        return predicate switch
        {
            SpatialJoinPredicate.JoinContainsTarget => joinGeometry.Contains(targetGeometry),
            SpatialJoinPredicate.TargetContainsJoin => targetGeometry.Contains(joinGeometry),
            SpatialJoinPredicate.DWithin when isGeographic => IsWithinGeodesicMeters(joinGeometry, targetGeometry, distance),
            SpatialJoinPredicate.DWithin => joinGeometry.IsWithinDistance(targetGeometry, distance),
            _ => joinGeometry.Intersects(targetGeometry),
        };
    }

    private static bool IsWithinGeodesicMeters(NtsGeometry first, NtsGeometry second, double distanceMeters)
    {
        if (first.Intersects(second))
        {
            return true;
        }

        var nearest = DistanceOp.NearestPoints(first, second);
        if (nearest is null || nearest.Length < 2)
        {
            return false;
        }

        var meters = GeodesicGeometryOperations.ComputeEllipsoidalDistanceMeters(
            nearest[0].Y, nearest[0].X,
            nearest[1].Y, nearest[1].X);
        return double.IsFinite(meters) && meters <= distanceMeters;
    }
}
