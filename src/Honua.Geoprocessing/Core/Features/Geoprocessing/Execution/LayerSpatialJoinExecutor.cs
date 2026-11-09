// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class LayerSpatialJoinExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "analytics.spatial-join";

    internal const string JoinCountAttribute = SpatialJoinSupport.JoinCountAttribute;

    public LayerSpatialJoinExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerSpatialJoinExecutor> logger,
        IOptions<LimitsOptions>? limitsOptions = null)
        : base(serviceScopeFactory, options, logger, limitsOptions)
    {
    }

    protected override string ProcessId => HandledProcessId;

    private protected override int? ResolveStatisticsLayerId(StepInputReader inputs, DagSourceRequest request)
        => RequireLayerId(inputs, "joinLayerId");

    private protected override async Task<List<IFeature>> ApplyCoreAsync(
        LayerOpContext context,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var (predicate, distance) = ReadPredicate(inputs);
        var carryFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("carryFields", string.Empty));
        var stats = StatisticsSupport.ParseOutStatistics(inputs.GetOrDefault("outStatistics", string.Empty));
        StatisticsSupport.EnsureNoOutputCollisions(stats, [.. carryFields, JoinCountAttribute]);

        // Resolve the second (join) layer through the same source.honua-layer connector
        // that streamed the target layer. Only the join layer id windows this read; the
        // shared analytics where/bbox filters apply to the target layer. Bounded by the
        // SAME admission limits as the target layer (#4629): a two-layer op that only
        // bounded one side would let the unbounded side alone destabilize the worker.
        var joinLayerId = RequireLayerId(inputs, "joinLayerId");
        var joinRequest = new DagSourceRequest { LayerId = joinLayerId };
        var joinFeatures = await ReadLayerAsync(
                context.LayerSource,
                joinRequest,
                cancellationToken,
                Limits.Analytics.MaxInputFeatures,
                $"join layer {joinLayerId}",
                Limits.Geometry.MaxVerticesPerGeometry,
                Limits.Geometry.MaxGeometrySize,
                Limits.Analytics.MaxInputBytes,
                    Options.CurrentValue.MaxLayerVertices)
            .ConfigureAwait(false);

        LayerComputationBudget.EnsureTopologyWork(
            LayerComputationBudget.CountVertices(context.Features),
            LayerComputationBudget.CountVertices(joinFeatures),
            Options.CurrentValue.MaxTopologyWork);
        var index = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);

        // The join itself is a Cartesian product: two per-layer-admitted but broadly
        // overlapping layers can still evaluate far more candidate pairs and carry far more
        // matched values than either input's feature count implies (honua-server#3075). The
        // enrichment.enrich executor already charges this same shared budget; this executor
        // was the one two-layer op still passing budget: null (#4629).
        var budget = new SpatialJoinSupport.MatchBudget(SpatialJoinSupport.DefaultMaxCarriedMatchValues);

        var output = new List<IFeature>(context.Features.Count);
        foreach (var target in context.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The token also goes INTO the join: a single target against a broadly
            // overlapping dataset can spend a long time in the candidate loop, so checking
            // only between targets left a dismissed job running (honua-server#3075).
            output.Add(SpatialJoinSupport.Join(
                target, index, predicate, distance, carryFields, stats, budget, cancellationToken));
        }

        return output;
    }

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
