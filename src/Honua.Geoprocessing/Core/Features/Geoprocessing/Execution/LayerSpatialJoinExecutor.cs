// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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

    private protected override int? ResolveFieldLayerId(StepInputReader inputs, DagSourceRequest request)
        => RequireLayerId(inputs, "joinLayerId");

    private protected override IEnumerable<(string Parameter, string Field)> ReadFieldReferences(StepInputReader inputs)
        => base.ReadFieldReferences(inputs).Concat(
            StatisticsSupport.ParseFieldList(inputs.GetOrDefault("carryFields", string.Empty))
                .Select(field => ("carryFields", field)));

    private protected override async Task<List<IFeature>> ApplyCoreAsync(
        LayerOpContext context,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var (predicate, distance) = ReadPredicate(inputs);
        var carryFields = StatisticsSupport.ParseFieldList(inputs.GetOrDefault("carryFields", string.Empty));
        var stats = StatisticsSupport.ParseOutStatistics(inputs.GetOrDefault("outStatistics", string.Empty));
        StatisticsSupport.EnsureNoOutputCollisions(stats, [.. carryFields, JoinCountAttribute]);

        // Request the join layer in the target layer's SRID so cross-SRID joins evaluate in a common CRS.
        var targetSrid = await ResolveTargetSridAsync(context, cancellationToken).ConfigureAwait(false);
        var joinLayerId = RequireLayerId(inputs, "joinLayerId");
        var joinRequest = new DagSourceRequest
        {
            LayerId = joinLayerId,
            Where = ReadNonBlank(inputs, "joinWhere"),
            OutputSrid = targetSrid,
        };
        var joinFeatures = await ReadJoinLayerAsync(context.LayerSource, joinRequest, cancellationToken)
            .ConfigureAwait(false);

        LayerComputationBudget.EnsureTopologyWork(
            LayerComputationBudget.CountVertices(context.Features),
            LayerComputationBudget.CountVertices(joinFeatures),
            Options.CurrentValue.MaxTopologyWork);
        var index = SpatialJoinSupport.BuildIndex(joinFeatures, cancellationToken);

        // The join itself is a Cartesian product: two per-layer-admitted but broadly
        // overlapping layers can still evaluate far more candidate pairs and carry far more
        // matched values than either input's feature count implies. The
        // enrichment.enrich executor already charges this same shared budget; this executor
        // was the one two-layer op still passing budget: null.
        var budget = new SpatialJoinSupport.MatchBudget(SpatialJoinSupport.DefaultMaxCarriedMatchValues);

        var output = new List<IFeature>(context.Features.Count);
        foreach (var target in context.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The token also goes INTO the join: a single target against a broadly
            // overlapping dataset can spend a long time in the candidate loop, so checking
            // only between targets left a dismissed job running.
            output.Add(SpatialJoinSupport.Join(
                target, index, predicate, distance, carryFields, stats, budget, targetSrid, cancellationToken));
        }

        return output;
    }

    // joinWhere narrows the join layer before the per-layer vertex budget and the topology
    // budget are charged, through the same parameterized where translation as the target's
    // `where`. Without it a large reference layer (a county flood map) could never be joined.
    private async Task<List<IFeature>> ReadJoinLayerAsync(
        IDagFeatureSource layerSource,
        DagSourceRequest joinRequest,
        CancellationToken cancellationToken)
    {
        var joinLayerId = joinRequest.LayerId;
        try
        {
            return await ReadLayerAsync(
                    layerSource,
                    joinRequest,
                    cancellationToken,
                    Limits.Analytics.MaxInputFeatures,
                    $"join layer {joinLayerId}",
                    Limits.Geometry.MaxVerticesPerGeometry,
                    Limits.Geometry.MaxGeometrySize,
                    Limits.Analytics.MaxInputBytes,
                    Options.CurrentValue.MaxLayerVertices)
                .ConfigureAwait(false);
        }
        catch (DagSourceSelectionException ex)
        {
            // Raised only for a caller-written selector, with fixed guidance; name which one.
            throw new TransformInputException($"joinWhere on join layer {joinLayerId}: {ex.Message}", ex);
        }
        catch (TransformInputException ex) when (joinRequest.Where is null)
        {
            // The budget messages say "narrow the selection"; name the parameter that does it.
            throw new TransformInputException($"{ex.PublicMessage} Filter the join layer with joinWhere.", ex);
        }
    }

    private static async Task<int?> ResolveTargetSridAsync(
        LayerOpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.OutputSrid is > 0)
        {
            return context.Request.OutputSrid;
        }

        var fromFeature = context.Features
            .Select(feature => feature.Geometry)
            .FirstOrDefault(geometry => geometry is { IsEmpty: false, SRID: > 0 })
            ?.SRID;
        if (fromFeature is > 0)
        {
            return fromFeature;
        }

        var metadata = context.Services.GetService<IMetadataV2GraphProvider>();
        if (metadata is not null && context.Request.LayerId is { } layerId)
        {
            var snapshot = await metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var storageSrid = snapshot.ResolveStorageSrid(layerId);
            if (storageSrid is > 0)
            {
                return storageSrid;
            }
        }

        return null;
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
