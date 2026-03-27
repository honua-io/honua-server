// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderProjectedPointTests
{
    [Fact]
    public void BuildProjectedPointQuery_WithRasterGrid_UsesDistinctPointCellsAndLimit()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326,
            Limit = 10,
            RasterPointGrid = new RasterPointGrid
            {
                OriginX = -180,
                OriginY = 90,
                CellWidth = 1.5,
                CellHeight = 1.5
            }
        };

        var result = queryBuilder.BuildProjectedPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("WITH point_source AS");
        result.Sql.Should().Contain("SELECT DISTINCT ON (cell_x, cell_y) x, y FROM snapped_points");
        result.Sql.Should().Contain("FLOOR((ST_X(point_geom) - $2) / $3)::bigint AS cell_x");
        result.Sql.Should().Contain("FLOOR(($4 - ST_Y(point_geom)) / $5)::bigint AS cell_y");
        result.Sql.Should().Contain("LIMIT $6");
        result.WhereParameters.Should().Equal(-180d, 1.5d, 90d, 1.5d);
    }

    [Fact]
    public void BuildProjectedPointQuery_WithoutRasterGrid_SelectsProjectedCoordinates()
    {
        var queryBuilder = CreateQueryBuilder();
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326
        };

        var result = queryBuilder.BuildProjectedPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("SELECT ST_X(point_geom) AS x, ST_Y(point_geom) AS y FROM point_source ORDER BY objectid");
        result.Sql.Should().NotContain("DISTINCT ON (cell_x, cell_y)");
        result.WhereParameters.Should().BeEmpty();
    }

    private static FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        return new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);
    }
}
