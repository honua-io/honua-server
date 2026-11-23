// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class DatabricksH3QueryBuilderTests
{
    private static DatabricksLayerMapping BuildMapping(int srid = 4326) => new()
    {
        LayerId = 1,
        Table = "parcels",
        Catalog = "main",
        Schema = "gis",
        GeometryColumn = "geom",
        PrimaryKeyColumn = "id",
        Srid = srid,
        GeometryType = GeometryType.Polygon,
        AttributeColumns = ["name", "area"],
    };

    [Fact]
    public void BuildH3Aggregation_BasicQuery_EmitsDatabricksLonLatH3Sql()
    {
        var statement = DatabricksFeatureQueryBuilder.BuildH3Aggregation(
            BuildMapping(),
            new FeatureQuery { Where = "name = 'north'" },
            new H3AggregationQuery
            {
                Resolution = 7,
                MaxCells = 300,
            });

        Assert.Contains("h3_longlatash3string(st_x(st_centroid(`geom`)), st_y(st_centroid(`geom`)), :h3_res)", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("h3_boundaryasgeojson(_h3cell) AS `cellGeometry`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(`id`) AS `count`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 300", statement.Sql, StringComparison.Ordinal);
        Assert.Contains(statement.Parameters, p => p.Name == "h3_res" && p.Value == "7" && p.Type == "INT");
        Assert.Contains(statement.Parameters, p => p.Value == "north");
    }

    [Fact]
    public void BuildH3Aggregation_WithPolyfillAndKRing_EmitsExplodedH3Sql()
    {
        var statement = DatabricksFeatureQueryBuilder.BuildH3Aggregation(
            BuildMapping(),
            new FeatureQuery(),
            new H3AggregationQuery
            {
                Resolution = 6,
                PolyfillGeometry = [0x01, 0x03, 0x00, 0x00, 0x00],
                KRingDistance = 1,
                OutStatistics = ImmutableArray.Create(new StatisticDefinition
                {
                    StatisticType = StatisticType.Avg,
                    OnStatisticField = "area",
                    OutStatisticFieldName = "avg_area",
                }),
            });

        Assert.Contains("EXPLODE(h3_polyfillash3string(st_astext(st_geomfromwkb(unhex(:h3_polyfill), 4326)), :h3_res))", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("LATERAL VIEW EXPLODE(h3_kring(a.`cellIndex`, :h3_kring)) _k AS neighbor", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("AVG(a.`avg_area`) AS `avg_area`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains(statement.Parameters, p => p.Name == "h3_kring" && p.Value == "1" && p.Type == "INT");
    }

    [Fact]
    public void BuildH3Aggregation_Non4326Layer_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => DatabricksFeatureQueryBuilder.BuildH3Aggregation(
            BuildMapping(srid: 3857),
            new FeatureQuery(),
            new H3AggregationQuery { Resolution = 5 }));
    }
}
