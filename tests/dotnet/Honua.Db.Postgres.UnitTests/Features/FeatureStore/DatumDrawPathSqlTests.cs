// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Raster;
using Honua.Db.Postgres.Features.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.UnitTests.Features.FeatureStore;

/// <summary>
/// Draw paths use the datum catalog. No catalog entry keeps two-argument ST_Transform.
/// NAD83 to WGS 84 stays the catalog no-operation.
/// </summary>
public sealed class DatumDrawPathSqlTests
{
    private static readonly IDatumTransformationCatalog Catalog = EsriDatumTransformationCatalog.Create();

    [UnitTest]
    public void Mvt_Nad27ToWebMercator_UsesTheGridThatContainsTheTile()
    {
        var conus = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4267 }, 56, 101, 8);
        var alaska = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4267 }, 21, 68, 8);
        var world = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4267 }, 0, 0, 0);

        conus.Should().Contain("us_noaa_conus.tif");
        conus.Should().NotContain("us_noaa_alaska.tif");
        alaska.Should().Contain("us_noaa_alaska.tif");
        alaska.Should().NotContain("us_noaa_conus.tif");
        world.Should().NotContain("ST_TransformPipeline(");
        world.Should().Contain("ST_Transform(");
    }

    [UnitTest]
    public void Mvt_Nad27ToWorldCrs84Quad_UsesTheGridThatContainsTheTile()
    {
        var conus = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4267, OutputSrid = 4326 }, 14, 9, 5);
        var alaska = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4267, OutputSrid = 4326 }, 5, 4, 5);

        conus.Should().Contain("us_noaa_conus.tif");
        conus.Should().NotContain("us_noaa_alaska.tif");
        alaska.Should().Contain("us_noaa_alaska.tif");
        alaska.Should().NotContain("us_noaa_conus.tif");
    }

    [UnitTest]
    public void Mvt_UnknownPair_KeepsTwoArgumentTransform()
    {
        var sql = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 2154 }, 56, 101, 8);

        sql.Should().Contain("ST_Transform(");
        sql.Should().NotContain("ST_TransformPipeline(");
        DatumOutputSelection.Resolve(Catalog, 2154, 3857).Should().BeNull();
        // Web Mercator and WGS 84 differ by a projection. The NAD83 no-operation is not a grid.
        DatumOutputSelection.Resolve(Catalog, 3857, 4326).Should().BeNull();
        DatumOutputSelection.Resolve(Catalog, 4326, 3857).Should().BeNull();
    }

    [UnitTest]
    public void Mvt_Nad83ToWgs84_StaysNoOperation()
    {
        var sql = BuildMvt(new FeatureQuery { SpatialReferenceSrid = 4269, OutputSrid = 4326 }, 14, 9, 5);

        sql.Should().Contain("ST_SetSRID(");
        sql.Should().NotContain("hgridshift");
        sql.Should().NotContain("ST_TransformPipeline(");
    }

    [UnitTest]
    public void Scene_Nad27_UsesCatalogGrid_AndUnknownPairDoesNot()
    {
        PostgresSceneFeatureSource.BuildPublishedGeometrySql(4267).Should().Contain("us_noaa_conus.tif");
        PostgresSceneFeatureSource.BuildPublishedGeometrySql(2154).Should().Be("ST_Transform(geometry, 4326)");
        PostgresSceneFeatureSource.BuildPublishedGeometrySql(4269).Should().Contain("ST_SetSRID(");
        PostgresSceneFeatureSource.BuildPublishedGeometrySql(4326).Should().Be("ST_Transform(geometry, 4326)");
    }

    [UnitTest]
    public void Identify_Nad27Raster_UsesCatalogGrid_AndUnknownPairDoesNot()
    {
        DatumSamplePointSql.Build(3857, 4267).Should().Contain("us_noaa_conus.tif");
        DatumSamplePointSql.Build(4326, 4267).Should().Contain("ST_TransformPipeline(");
        DatumSamplePointSql.Build(3857, 2154).Should().Be(
            "ST_Transform(ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid), 2154)");
        DatumSamplePointSql.Build(4326, 4326).Should().Be("ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid)");
    }

    private static string BuildMvt(FeatureQuery query, int x, int y, int z)
    {
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor()).BuildMvtTileQuery(
            1, x, y, z,
            query,
            new TileOptions { TileBuffer = 0, TileExtent = 4096, SimplifyZoom = -1 },
            new TileLimits()).Sql;
    }
}
