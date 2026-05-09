// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderH3SummaryTests
{
    [Fact]
    public void BuildH3AggregationQuery_WithSdkSummaryDefinitions_EmitsMetricAndBucketSummaryColumns()
    {
        var queryBuilder = CreateQueryBuilder();
        var h3Query = new H3AggregationQuery
        {
            Resolution = 7,
            SummaryDefinitions = ImmutableArray.Create(
                new SpatialAggregationSummaryDefinition
                {
                    Id = "featureCount",
                    Kind = SpatialAggregationSummaryKind.Count
                },
                new SpatialAggregationSummaryDefinition
                {
                    Id = "populationSum",
                    Kind = SpatialAggregationSummaryKind.Sum,
                    Field = "population",
                    FieldType = FieldType.Integer
                },
                new SpatialAggregationSummaryDefinition
                {
                    Id = "byStatus",
                    Kind = SpatialAggregationSummaryKind.Category,
                    Field = "status",
                    CategoryBuckets = ImmutableArray.Create(
                        new SpatialAggregationCategoryBucketDefinition { Value = "open", Label = "Open" },
                        new SpatialAggregationCategoryBucketDefinition { Value = "closed", Label = "Closed" })
                },
                new SpatialAggregationSummaryDefinition
                {
                    Id = "scoreHistogram",
                    Kind = SpatialAggregationSummaryKind.Histogram,
                    Field = "score",
                    FieldType = FieldType.Double,
                    HistogramBins = 2,
                    HistogramMin = 0,
                    HistogramMax = 100
                },
                new SpatialAggregationSummaryDefinition
                {
                    Id = "riskRanges",
                    Kind = SpatialAggregationSummaryKind.Range,
                    Field = "risk",
                    FieldType = FieldType.Double,
                    Ranges = ImmutableArray.Create(
                        new SpatialAggregationRangeBucketDefinition
                        {
                            Id = "low",
                            Label = "Low",
                            Min = 0,
                            Max = 50,
                            IncludeMin = true,
                            IncludeMax = false
                        },
                        new SpatialAggregationRangeBucketDefinition
                        {
                            Id = "high",
                            Label = "High",
                            Min = 50,
                            Max = 100,
                            IncludeMin = true,
                            IncludeMax = true
                        })
                })
        };

        var result = queryBuilder.BuildH3AggregationQuery(layerId: 1, new FeatureQuery(), h3Query);

        result.Sql.Should().Contain("COUNT(objectid) AS \"featureCount\"");
        result.Sql.Should().Contain("SUM((attributes->>'population')::numeric) AS \"populationSum\"");
        result.Sql.Should().Contain("jsonb_build_object('kind', 'category'");
        result.Sql.Should().Contain("AS \"byStatus\"");
        result.Sql.Should().Contain("jsonb_build_object('kind', 'histogram'");
        result.Sql.Should().Contain("AS \"scoreHistogram\"");
        result.Sql.Should().Contain("jsonb_build_object('kind', 'range'");
        result.Sql.Should().Contain("AS \"riskRanges\"");
        result.Sql.Should().Contain("'otherCount'");
        result.Sql.Should().Contain("'nullCount'");
        result.Sql.Should().Contain("'includeMax', true");
        result.WhereParameters.Should().Contain("open");
        result.WhereParameters.Should().Contain("closed");
        result.WhereParameters.Should().Contain("low");
        result.WhereParameters.Should().Contain("high");
    }

    [Fact]
    public void BuildH3AggregationQuery_WithSdkSummaryDefinitions_ReaggregatesMetricSummariesForKRing()
    {
        var queryBuilder = CreateQueryBuilder();
        var h3Query = new H3AggregationQuery
        {
            Resolution = 7,
            KRingDistance = 1,
            SummaryDefinitions = ImmutableArray.Create(
                new SpatialAggregationSummaryDefinition
                {
                    Id = "featureCount",
                    Kind = SpatialAggregationSummaryKind.Count
                },
                new SpatialAggregationSummaryDefinition
                {
                    Id = "maxScore",
                    Kind = SpatialAggregationSummaryKind.Max,
                    Field = "score",
                    FieldType = FieldType.Double
                })
        };

        var result = queryBuilder.BuildH3AggregationQuery(layerId: 1, new FeatureQuery(), h3Query);

        result.Sql.Should().Contain("WITH aggregated AS (");
        result.Sql.Should().Contain("SUM(a.\"featureCount\") AS \"featureCount\"");
        result.Sql.Should().Contain("MAX(a.\"maxScore\") AS \"maxScore\"");
        result.Sql.Should().Contain("LATERAL h3_grid_disk");
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
