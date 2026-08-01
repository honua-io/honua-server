// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NtsEnvelope = NetTopologySuite.Geometries.Envelope;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.spatial-join</c> layer-aware executor (#2322). The job-executable
/// counterpart of the layer-scoped PostGIS <c>SpatialAnalytics</c> spatial join, and
/// the two-layer sibling of the single-layer <see cref="LayerSourcedFeatureExecutor"/>
/// ops: it resolves BOTH the target layer (<c>layerId</c>) AND the reference/join
/// layer (<c>joinLayerId</c>) through the same <c>source.honua-layer</c> connector,
/// then enriches every target feature with attributes and aggregates drawn from the
/// join features that satisfy a spatial predicate — all in one dispatched job.
///
/// <para>
/// Predicates reuse NetTopologySuite's managed relational operators (no GEOS/GDAL
/// native dependency) over the canonical <see cref="SpatialJoinPredicate"/> members
/// shared with the PostGIS pushdown (honua-server#3069): <c>intersects</c> (default),
/// <c>contains</c> (<see cref="SpatialJoinPredicate.JoinContainsTarget"/> — the join
/// geometry contains the target, the classic point-in-polygon case), <c>within</c>
/// (<see cref="SpatialJoinPredicate.TargetContainsJoin"/> — the target contains the
/// join geometry), and <c>dwithin</c> (the join geometry is within <c>distance</c> of
/// the target). Distances are evaluated in the CRS units of
/// the supplied geometries — geodesic conversion is not performed, matching the other
/// managed layer-aware analytics executors. Candidate join features are pruned through
/// an in-memory <see cref="STRtree{T}"/> index before the exact predicate test.
/// </para>
///
/// <para>
/// Each target feature carries its original attributes plus a <c>JOIN_COUNT</c> of the
/// matched join rows. For every column named in <c>carryFields</c> the matched join
/// values are emitted as an array attribute of the same name (empty array on zero
/// matches), and any <c>outStatistics</c> <c>field:stat</c> aggregates are computed
/// over the matched join rows through the shared <see cref="StatisticsSupport"/>
/// accumulators. Targets with no match are preserved one-to-one with a
/// <c>JOIN_COUNT</c> of 0.
/// </para>
/// </summary>
internal sealed class LayerSpatialJoinExecutor : LayerSourcedFeatureExecutor
{
    /// <summary>The dotted process id this executor handles.</summary>
    internal const string HandledProcessId = "analytics.spatial-join";

    /// <summary>Attribute holding the count of matched join features on each target.</summary>
    internal const string JoinCountAttribute = "JOIN_COUNT";

    /// <summary>Initializes a new instance of the <see cref="LayerSpatialJoinExecutor"/> class.</summary>
    public LayerSpatialJoinExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerSpatialJoinExecutor> logger)
        : base(serviceScopeFactory, options, logger)
    {
    }

    /// <inheritdoc />
    protected override string ProcessId => HandledProcessId;

    /// <inheritdoc />
    private protected override async Task<List<IFeature>> ApplyCoreAsync(
        LayerOpContext context,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var (predicate, distance) = ReadPredicate(inputs);
        var carryFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("carryFields", string.Empty));
        var stats = StatisticsSupport.ParseStatistics(inputs.GetOrDefault("outStatistics", string.Empty));

        // Resolve the second (join) layer through the same source.honua-layer connector
        // that streamed the target layer. Only the join layer id windows this read; the
        // shared analytics where/bbox filters apply to the target layer.
        var joinLayerId = RequireLayerId(inputs, "joinLayerId");
        var joinRequest = new DagSourceRequest { LayerId = joinLayerId };
        var joinFeatures = await ReadLayerAsync(context.LayerSource, joinRequest, cancellationToken)
            .ConfigureAwait(false);

        var index = BuildIndex(joinFeatures, cancellationToken);

        var output = new List<IFeature>(context.Features.Count);
        foreach (var target in context.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(Join(target, index, predicate, distance, carryFields, stats));
        }

        return output;
    }

    private static Feature Join(
        IFeature target,
        STRtree<IFeature> index,
        SpatialJoinPredicate predicate,
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

    private static STRtree<IFeature> BuildIndex(List<IFeature> joinFeatures, CancellationToken cancellationToken)
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

        // The canonical member names carry the operand order, so the managed
        // evaluation cannot drift from the PostGIS pushdown (honua-server#3069).
        return predicate switch
        {
            SpatialJoinPredicate.JoinContainsTarget => joinGeometry.Contains(targetGeometry),
            SpatialJoinPredicate.TargetContainsJoin => targetGeometry.Contains(joinGeometry),
            SpatialJoinPredicate.DWithin => joinGeometry.IsWithinDistance(targetGeometry, distance),
            _ => joinGeometry.Intersects(targetGeometry),
        };
    }

    private static object? ReadValue(IFeature feature, string field)
        => feature.Attributes is not null && feature.Attributes.Exists(field)
            ? feature.Attributes.GetOptionalValue(field)
            : null;

    // Wire vocabulary for this process is JOIN-SUBJECT: `contains` is the
    // point-in-polygon direction (the join/reference geometry contains the target).
    // Unchanged behavior — the mapping is now explicit about which operand leads.
    private static (SpatialJoinPredicate Predicate, double Distance) ReadPredicate(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("predicate", "intersects").Trim().ToLowerInvariant();
        var predicate = raw switch
        {
            "" or "intersects" => SpatialJoinPredicate.Intersects,
            "contains" => SpatialJoinPredicate.JoinContainsTarget,
            "within" => SpatialJoinPredicate.TargetContainsJoin,
            "dwithin" => SpatialJoinPredicate.DWithin,
            _ => throw new TransformInputException(
                $"predicate '{raw}' is not supported (allowed: intersects, contains, within, dwithin)"),
        };

        var distance = 0d;
        if (predicate == SpatialJoinPredicate.DWithin
            && (!inputs.TryGet("distance", out var distanceRaw)
                || !double.TryParse(distanceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
                || !double.IsFinite(distance)
                || distance <= 0))
        {
            throw new TransformInputException(
                "predicate 'dwithin' requires a finite positive 'distance' threshold");
        }

        return (predicate, distance);
    }
}
