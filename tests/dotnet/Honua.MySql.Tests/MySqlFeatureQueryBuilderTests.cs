// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Tests;

/// <summary>
/// Unit tests for MySQL/MariaDB SQL query generation. Verifies backtick-quoted
/// identifiers, named parameter binding, spatial filter translation, and the
/// NotSupportedException contract for out-of-scope operations.
/// </summary>
public class MySqlFeatureQueryBuilderTests
{
    private const int LayerId = 1;
    private readonly MySqlFeatureQueryBuilder _builder;

    public MySqlFeatureQueryBuilderTests()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = LayerId,
            TableName = "parcels",
            SchemaName = "honua",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name", "area", "type"],
            GeometryType = GeometryType.Polygon
        };

        var registry = new MySqlLayerMappingRegistry([mapping]);
        _builder = new MySqlFeatureQueryBuilder(registry);
    }

    [Fact]
    public void BuildSelectQuery_DefaultQuery_QuotesIdentifiersAndProjectsAttributes()
    {
        var result = _builder.BuildSelectQuery(LayerId, new FeatureQuery());

        Assert.Contains("SELECT `id`, ST_AsWKB(`geom`)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("`name`, `area`, `type`", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `honua`.`parcels`", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithOutFields_OnlyProjectsRequestedColumns()
    {
        var query = new FeatureQuery
        {
            OutFields = ImmutableArray.Create("name", "area")
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("`name`, `area`", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`type`", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_ExcludeAttributes_OmitsAttributeColumns()
    {
        var query = new FeatureQuery { ExcludeAttributes = true };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.DoesNotContain("`name`", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`area`", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`type`", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithPagination_GeneratesNamedLimitOffsetParameters()
    {
        var query = new FeatureQuery { Limit = 10, Offset = 20 };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("LIMIT @p0", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET @p1", result.Sql, StringComparison.Ordinal);
        Assert.Equal(10, result.WhereParameters[0]);
        Assert.Equal(20, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_WithObjectIds_GeneratesInClause()
    {
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L, 2L, 3L)
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("`id` IN (@p0, @p1, @p2)", result.Sql, StringComparison.Ordinal);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildSelectQuery_WithSqlFilter_RenumbersEmbeddedParameters()
    {
        var fragment = new SqlFragment("`name` = @p0", new object?[] { "Acme" });
        var query = new FeatureQuery { SqlFilter = fragment, Limit = 5 };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("AND (`name` = @p0)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p1", result.Sql, StringComparison.Ordinal);
        Assert.Equal("Acme", result.WhereParameters[0]);
        Assert.Equal(5, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_WithWhereClause_ParameterizesComparison()
    {
        var query = new FeatureQuery { Where = "type = 'commercial'" };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("AND (`type` = @p0)", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("commercial", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_WithIntersectsSpatialFilter_AddsMbrAndStIntersectsClauses()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01, 0x02, 0x03],
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 4326)
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_Intersects(`geom`, ST_GeomFromWKB(@p0, 4326))", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithEnvelopeIntersects_UsesMbrOnly()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01],
                spatialRelationship: SpatialRelationship.EnvelopeIntersects,
                srid: 4326)
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326))", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_Intersects", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_CrossSridFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01],
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 3857)
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildSelectQuery(LayerId, query));
        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4326", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_NearestNeighborFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateKnnFilter([0x01], count: 5, srid: 4326)
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildSelectQuery(LayerId, query));
        Assert.Contains("Nearest-neighbor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_OutputSridDifferentFromLayer_ThrowsNotSupported()
    {
        var query = new FeatureQuery { OutputSrid = 3857 };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildSelectQuery(LayerId, query));
        Assert.Contains("Output SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_DistanceFilter_OnPolygonLayer_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter([0x01], distance: 100, srid: 4326)
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildSelectQuery(LayerId, query));
        Assert.Contains("point layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_GeneratesCorrectSql()
    {
        var result = _builder.BuildCountQuery(LayerId, new FeatureQuery());

        Assert.Contains("SELECT COUNT(*) FROM `honua`.`parcels` WHERE 1=1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_PolygonLayer_UsesPerRowEnvelopeWithSridReset()
    {
        // Default test layer is Polygon; envelope corners come via ST_PointN(ST_ExteriorRing(...))
        var result = _builder.BuildExtentQuery(LayerId, new FeatureQuery());

        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(ST_SRID(`geom`, 0))), 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(ST_SRID(`geom`, 0))), 3)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `honua`.`parcels`", result.Sql, StringComparison.Ordinal);
        Assert.Contains("`geom` IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_PointLayer_UsesDirectStXY()
    {
        var pointMapping = new MySqlLayerMapping
        {
            LayerId = 99,
            TableName = "stations",
            GeometryColumn = "loc",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.Point
        };
        var registry = new MySqlLayerMappingRegistry([pointMapping]);
        var builder = new MySqlFeatureQueryBuilder(registry);

        var result = builder.BuildExtentQuery(99, new FeatureQuery());

        Assert.Contains("MIN(ST_X(ST_SRID(`loc`, 0)))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_Y(ST_SRID(`loc`, 0)))", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_Envelope", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_WithSpatialFilter_AppendsSpatialClause()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01],
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 4326)
        };

        var result = _builder.BuildExtentQuery(LayerId, query);

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326))", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_GeneratesCorrectSql()
    {
        var result = _builder.BuildObjectIdsQuery(LayerId, new FeatureQuery());

        Assert.Contains("SELECT `id` FROM `honua`.`parcels`", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithOrderBy_QuotesFields()
    {
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(new OrderByClause("name", ascending: true))
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("ORDER BY `name` ASC", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectGmlQuery_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _builder.BuildSelectGmlQuery(LayerId, new FeatureQuery()));
        Assert.Contains("BuildSelectGmlQuery", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MySQL/MariaDB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatisticsQuery_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => _builder.BuildStatisticsQuery(LayerId, new FeatureQuery()));
        Assert.Contains("BuildStatisticsQuery", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMvtTileQuery_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildMvtTileQuery(
            LayerId, 0, 0, 0, null,
            new Honua.Core.Features.Tiles.TileOptions(),
            new Honua.Core.Configuration.TileLimits()));
        Assert.Contains("MySQL/MariaDB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MySqlFeatureQueryBuilder(null!));
    }

    [Fact]
    public void BuildSelectQuery_UnregisteredLayer_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => _builder.BuildSelectQuery(999, new FeatureQuery()));
    }
}
