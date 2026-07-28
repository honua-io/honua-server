// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

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
/// The join computation itself is the shared <see cref="SpatialJoinSupport"/>
/// (also consumed by the <c>enrichment.enrich</c> executor, #2283): managed NTS
/// relational predicates — <c>intersects</c> (default), <c>contains</c> (the join
/// geometry contains the target — the classic point-in-polygon case), <c>within</c>
/// (the target contains the join geometry), and <c>dwithin</c> (the join geometry is
/// within <c>distance</c> of the target). Distances are evaluated in the CRS units of
/// the supplied geometries — geodesic conversion is not performed, matching the other
/// managed layer-aware analytics executors. Candidate join features are pruned through
/// an in-memory STRtree index before the exact predicate test.
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
    internal const string JoinCountAttribute = SpatialJoinSupport.JoinCountAttribute;

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

        var index = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);

        var output = new List<IFeature>(context.Features.Count);
        foreach (var target in context.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(SpatialJoinSupport.Join(target, index, predicate, distance, carryFields, stats));
        }

        return output;
    }

    private static (SpatialJoinSupport.SpatialPredicate Predicate, double Distance) ReadPredicate(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("predicate", "intersects").Trim().ToLowerInvariant();
        var predicate = raw switch
        {
            "" or "intersects" => SpatialJoinSupport.SpatialPredicate.Intersects,
            "contains" => SpatialJoinSupport.SpatialPredicate.Contains,
            "within" => SpatialJoinSupport.SpatialPredicate.Within,
            "dwithin" => SpatialJoinSupport.SpatialPredicate.Dwithin,
            _ => throw new TransformInputException(
                $"predicate '{raw}' is not supported (allowed: intersects, contains, within, dwithin)"),
        };

        var distance = 0d;
        if (predicate == SpatialJoinSupport.SpatialPredicate.Dwithin
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
