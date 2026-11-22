// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Warehouse;
using Honua.Core.Features.Warehouse.Abstractions;
using Honua.Core.Features.Warehouse.Domain;
using Honua.Core.Features.Warehouse.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Core.Tests.Features.Warehouse;

[Trait("Tier", "Fast")]
public sealed class WarehouseQueryPlanExplainerTests
{
    private static readonly byte[] SampleEnvelopeWkb = [0x01, 0x03, 0x00, 0x00, 0x00];

    [Fact]
    public async Task ExplainAsync_SameInputs_ProducesDeterministicPlanHashAndRedactsAllParameters()
    {
        using var services = new ServiceCollection()
            .AddWarehouseAnalyticsServices()
            .BuildServiceProvider();
        var explainer = services.GetRequiredService<IWarehouseQueryPlanExplainer>();
        var request = new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.DuckDb,
            SourceId = "s3-parcels",
            TargetTableOrView = "parcels",
            BboxColumn = "bbox",
            ExternalSourceUri = "s3://county-data/parcels.parquet",
            LayerSrid = 4326,
            Query = new FeatureQuery
            {
                Where = "owner = 'Alice Sensitive' AND value > 42000",
                SpatialFilter = SpatialFilter.Create(
                    geometry: SampleEnvelopeWkb,
                    spatialRelationship: SpatialRelationship.Intersects,
                    srid: 4326,
                    isSimpleEnvelope: true,
                    envelopeMinX: -122.5,
                    envelopeMinY: 37.7,
                    envelopeMaxX: -122.3,
                    envelopeMaxY: 37.8),
                OutFields = ImmutableArray.Create("owner", "value"),
                Limit = 500,
            },
        };

        var plan1 = await explainer.ExplainAsync(request);
        var plan2 = await explainer.ExplainAsync(request);

        Assert.Equal(plan1.PlanHash, plan2.PlanHash);
        Assert.Equal(plan1.RedactedSql, plan2.RedactedSql);
        Assert.True(plan1.BboxPushdown);
        Assert.False(plan1.H3Pushdown);
        Assert.True(plan1.ReadOnly);
        Assert.Equal(500, plan1.EffectiveRowLimit);
        Assert.False(plan1.GuardrailCapped);
        Assert.Contains("spatial", plan1.RequiredExtensions);
        Assert.Contains("httpfs", plan1.RequiredExtensions);
        Assert.DoesNotContain("Alice Sensitive", plan1.RedactedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("42000", plan1.RedactedSql, StringComparison.Ordinal);
        Assert.All(plan1.Parameters, p => Assert.StartsWith("[REDACTED", p.RedactedValue, StringComparison.Ordinal));
        Assert.Contains(plan1.Predicates, p => p.Kind == "bbox_covering" && p.PushedDown);
        Assert.Contains(plan1.Predicates, p => p.Kind == "spatial" && p.PushedDown);
        Assert.Contains(plan1.Predicates, p => p.Kind == "attribute" && p.PushedDown);

        var json = plan1.ToRedactedJson();
        Assert.DoesNotContain("Alice Sensitive", json, StringComparison.Ordinal);
        Assert.Contains("\"bboxPushdown\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainAsync_DuckDbProjectedEnvelope_DoesNotReportSourceBboxPushdown()
    {
        using var services = new ServiceCollection()
            .AddWarehouseAnalyticsServices()
            .BuildServiceProvider();
        var explainer = services.GetRequiredService<IWarehouseQueryPlanExplainer>();
        var request = new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.DuckDb,
            SourceId = "s3-parcels",
            TargetTableOrView = "parcels",
            BboxColumn = "bbox",
            LayerSrid = 4326,
            Query = new FeatureQuery
            {
                SpatialFilter = SpatialFilter.Create(
                    geometry: SampleEnvelopeWkb,
                    spatialRelationship: SpatialRelationship.Intersects,
                    srid: 3857,
                    isSimpleEnvelope: true,
                    envelopeMinX: -13636638,
                    envelopeMinY: 4531198,
                    envelopeMaxX: -13614374,
                    envelopeMaxY: 4545279),
            },
        };

        var plan = await explainer.ExplainAsync(request);

        Assert.False(plan.BboxPushdown);
        Assert.DoesNotContain(".xmax", plan.RedactedSql, StringComparison.Ordinal);
        Assert.Contains(plan.Predicates, predicate => predicate.Kind == "spatial" && predicate.PushedDown);
    }

    [Fact]
    public async Task ExplainAsync_UnboundedOrExcessiveLimits_AppliesRowAndH3Guardrails()
    {
        var explainer = new WarehouseQueryPlanExplainer(new WarehouseSourceCapabilityProvider());
        var guardrails = new WarehouseQueryGuardrails
        {
            MaxRows = 250,
            MaxH3Cells = 1_000,
            CommandTimeoutSeconds = 30,
            MaxScanBytes = 64 * 1024 * 1024,
        };

        var selectPlan = await explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.Redshift,
            SourceId = "rs-parcels",
            TargetTableOrView = "public.parcels",
            Query = new FeatureQuery { Limit = 5_000 },
            Guardrails = guardrails,
        });

        Assert.Equal(250, selectPlan.EffectiveRowLimit);
        Assert.True(selectPlan.GuardrailCapped);
        Assert.NotEmpty(selectPlan.Warnings);
        Assert.Equal(guardrails, selectPlan.AppliedGuardrails);

        var h3Plan = await explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.Snowflake,
            SourceId = "sf-parcels",
            TargetTableOrView = "ANALYTICS.PUBLIC.PARCELS",
            Query = new FeatureQuery(),
            H3Query = new H3AggregationQuery
            {
                Resolution = 7,
                MaxCells = 25_000,
                KRingDistance = 1,
            },
            Guardrails = guardrails,
        });

        Assert.True(h3Plan.H3Pushdown);
        Assert.Equal("h3_aggregation", h3Plan.OperationKind);
        Assert.Equal(1_000, h3Plan.EffectiveMaxH3Cells);
        Assert.True(h3Plan.GuardrailCapped);
        Assert.Contains("H3_POINT_TO_CELL_STRING", h3Plan.RedactedSql, StringComparison.Ordinal);
        Assert.Contains(h3Plan.Predicates, p => p.Kind == "h3_aggregation" && p.PushedDown);
        Assert.Contains(h3Plan.Predicates, p => p.Kind == "h3_kring" && p.PushedDown);
    }

    [Fact]
    public async Task ExplainAsync_H3OnDatabricksAndDuckDb_ProducesDialectSpecificPlans()
    {
        var explainer = new WarehouseQueryPlanExplainer(new WarehouseSourceCapabilityProvider());

        var dbxPlan = await explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.Databricks,
            SourceId = "dbx-events",
            TargetTableOrView = "main.gis.events",
            Query = new FeatureQuery(),
            H3Query = new H3AggregationQuery { Resolution = 6, MaxCells = 500 },
        });

        Assert.True(dbxPlan.H3Pushdown);
        Assert.Contains("h3_longlatash3string", dbxPlan.RedactedSql, StringComparison.Ordinal);
        Assert.Contains("h3_boundaryasgeojson", dbxPlan.RedactedSql, StringComparison.Ordinal);

        var duckPlan = await explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.DuckDb,
            SourceId = "duck-s3",
            TargetTableOrView = "events",
            ExternalSourceUri = "s3://analytics-bucket/events.parquet",
            Query = new FeatureQuery(),
            H3Query = new H3AggregationQuery { Resolution = 8, MaxCells = 500 },
        });

        Assert.True(duckPlan.H3Pushdown);
        Assert.Contains("h3_latlng_to_cell", duckPlan.RedactedSql, StringComparison.Ordinal);
        Assert.Contains("h3", duckPlan.RequiredExtensions);
        Assert.Contains("httpfs", duckPlan.RequiredExtensions);
        Assert.Contains("spatial", duckPlan.RequiredExtensions);
    }

    [Fact]
    public async Task ExplainAsync_H3OnRedshiftOrExcludedProvider_ThrowsNotSupported()
    {
        var explainer = new WarehouseQueryPlanExplainer(new WarehouseSourceCapabilityProvider());

        await Assert.ThrowsAsync<NotSupportedException>(() => explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = DataProviderNames.Redshift,
            SourceId = "rs-1",
            TargetTableOrView = "public.parcels",
            Query = new FeatureQuery(),
            H3Query = new H3AggregationQuery { Resolution = 5 },
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => explainer.ExplainAsync(new WarehouseQueryPlanRequest
        {
            ProviderName = "bigquery",
            SourceId = "bq-1",
            TargetTableOrView = "dataset.parcels",
            Query = new FeatureQuery(),
        }));
    }
}
