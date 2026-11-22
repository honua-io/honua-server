// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Snowflake.Features.FeatureStore.Services;

namespace Honua.Db.Snowflake.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class SnowflakeH3QueryBuilderTests
{
    private static SnowflakeLayerMapping BuildMapping(
        SnowflakeGeometryColumnType columnType = SnowflakeGeometryColumnType.Geography,
        int? srid = 4326)
    {
        var storage = new LayerStorageMapping(
            TableName: "PARCELS",
            SchemaName: "PUBLIC",
            CatalogName: null,
            DatabaseName: "ANALYTICS",
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: srid,
            ProviderOptions: new Dictionary<string, string>
            {
                [SnowflakeProviderOptions.GeometryType] = columnType == SnowflakeGeometryColumnType.Geometry ? "geometry" : "geography",
            });

        return SnowflakeLayerMapping.FromStorage(7, storage);
    }

    [Fact]
    public void BuildH3AggregationQuery_GeographyColumn_EmitsSnowflakeNativeH3Sql()
    {
        var result = SnowflakeFeatureQueryBuilder.BuildH3AggregationQuery(
            BuildMapping(SnowflakeGeometryColumnType.Geography),
            new FeatureQuery { Where = "AREA > 10" },
            new H3AggregationQuery
            {
                Resolution = 8,
                MaxCells = 250,
            });

        Assert.Contains("H3_POINT_TO_CELL_STRING(ST_CENTROID(\"SHAPE\"), ?)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_ASGEOJSON(H3_CELL_TO_BOUNDARY(_h3cell)) AS \"cellGeometry\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(\"OBJECTID\") AS \"count\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 250", result.Sql, StringComparison.Ordinal);
        Assert.Equal(2, result.WhereParameters.Count);
        Assert.Equal(8, result.WhereParameters[0]);
        Assert.Equal(10L, Convert.ToInt64(result.WhereParameters[1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BuildH3AggregationQuery_GeometryColumnWithPolyfillAndKRing_EmitsFlattenedH3Pushdown()
    {
        var polyfill = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00 };
        var result = SnowflakeFeatureQueryBuilder.BuildH3AggregationQuery(
            BuildMapping(SnowflakeGeometryColumnType.Geometry, srid: 4326),
            new FeatureQuery(),
            new H3AggregationQuery
            {
                Resolution = 6,
                PolyfillGeometry = polyfill,
                KRingDistance = 2,
                OutStatistics = ImmutableArray.Create(new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "AREA",
                    OutStatisticFieldName = "TOTAL_AREA",
                }),
            });

        Assert.Contains("H3_POLYGON_TO_CELLS_STRINGS(TO_GEOGRAPHY(?, TRUE), ?)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("H3_LATLNG_TO_CELL_STRING(ST_Y(ST_CENTROID(\"SHAPE\")), ST_X(ST_CENTROID(\"SHAPE\")), ?)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("LATERAL FLATTEN(INPUT => H3_GRID_DISK(a.\"cellIndex\", ?))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(a.\"TOTAL_AREA\") AS \"TOTAL_AREA\"", result.Sql, StringComparison.Ordinal);
        Assert.Equal(4, result.WhereParameters.Count);
        Assert.Same(polyfill, result.WhereParameters[0]);
        Assert.Equal(6, result.WhereParameters[1]);
        Assert.Equal(6, result.WhereParameters[2]);
        Assert.Equal(2, result.WhereParameters[3]);
    }

    [Fact]
    public void BuildH3AggregationQuery_Non4326Geometry_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => SnowflakeFeatureQueryBuilder.BuildH3AggregationQuery(
            BuildMapping(SnowflakeGeometryColumnType.Geometry, srid: 3857),
            new FeatureQuery(),
            new H3AggregationQuery { Resolution = 5 }));
    }
}
