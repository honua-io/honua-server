// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Redshift.Features.FeatureStore.Services;

namespace Honua.Redshift.Tests;

/// <summary>
/// Ungated unit tests for Redshift SQL generation. Verifies ANSI double-quoted identifiers,
/// Npgsql-style named parameter binding, Redshift-native spatial function translation, paging,
/// and the NotSupportedException contract for out-of-scope operations. These tests require no
/// external Redshift service and run in normal CI.
/// </summary>
public class RedshiftFeatureQueryBuilderTests
{
    private const int LayerId = 1;
    private static readonly string[] _attributes = ["name", "area", "type"];

    private static RedshiftLayerMapping Mapping(
        RedshiftGeometryColumnType geometryType = RedshiftGeometryColumnType.Geometry,
        string? geometryColumn = "geom",
        int? srid = 4326)
        => new()
        {
            LayerId = LayerId,
            TableName = "parcels",
            SchemaName = "honua",
            GeometryColumn = geometryColumn,
            PrimaryKeyColumn = "id",
            Srid = srid,
            GeometryColumnType = geometryType
        };

    [Fact]
    public void BuildSelectQuery_DefaultQuery_QuotesIdentifiersAndProjectsAttributes()
    {
        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), new FeatureQuery(), _attributes);

        Assert.Contains("SELECT \"id\" AS \"__objectid\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_AsBinary(\"geom\") AS \"__geometry\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"name\", \"area\", \"type\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"honua\".\"parcels\"", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1=1", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_NoGeometryColumn_SelectsNullGeometry()
    {
        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(
            Mapping(geometryColumn: null), new FeatureQuery(), _attributes);

        Assert.Contains("CAST(NULL AS VARBYTE) AS \"__geometry\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_GeographyColumn_RoundTripsThroughWkb()
    {
        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(
            Mapping(geometryType: RedshiftGeometryColumnType.Geography), new FeatureQuery(), _attributes);

        Assert.Contains("ST_AsBinary(ST_GeomFromWKB(ST_AsBinary(\"geom\"))) AS \"__geometry\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithOutFields_OnlyProjectsRequestedColumns()
    {
        var query = new FeatureQuery { OutFields = ImmutableArray.Create("name", "area") };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("\"name\", \"area\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_ExcludeAttributes_OmitsAttributeColumns()
    {
        var query = new FeatureQuery { ExcludeAttributes = true };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.DoesNotContain("\"name\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"area\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithPagination_GeneratesLimitThenOffset()
    {
        var query = new FeatureQuery { Limit = 10, Offset = 20 };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("LIMIT @p0", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET @p1", result.Sql, StringComparison.Ordinal);
        Assert.Equal(10, result.WhereParameters[0]);
        Assert.Equal(20, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_WithObjectIds_GeneratesInClause()
    {
        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(1L, 2L, 3L) };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("\"id\" IN (@p0, @p1, @p2)", result.Sql, StringComparison.Ordinal);
        Assert.Equal(3, result.WhereParameters.Count);
    }

    [Fact]
    public void BuildSelectQuery_WithWhereClause_ParameterizesComparison()
    {
        var query = new FeatureQuery { Where = "type = 'commercial'" };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("AND (\"type\" = @p0)", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal("commercial", result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_WithNullCheck_RendersIsNull()
    {
        var query = new FeatureQuery { Where = "name IS NOT NULL" };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("\"name\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithSqlFilter_ThrowsNotSupported()
    {
        var fragment = new Honua.Core.Queries.Filters.SqlFragment("\"name\" = @p0", new object?[] { "Acme" });
        var query = new FeatureQuery { SqlFilter = fragment };

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
        Assert.Contains("PostGIS-flavored", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start_and_end = 1")]
    [InlineData("and_flag = 1")]
    [InlineData("end_and = 1")]
    public void BuildSelectQuery_UnderscoreIdentifierContainingAnd_ParsesAsSingleField(string whereClause)
    {
        var query = new FeatureQuery { Where = whereClause };
        var fieldName = whereClause.Split(' ')[0];

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains($"AND (\"{fieldName}\" = @p0)", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithIntersectsSpatialFilter_EmitsStIntersects()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01, 0x02, 0x03],
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 4326)
        };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("ST_Intersects(\"geom\", ST_GeomFromWKB(@p0, 4326))", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WithEnvelopeIntersects_UsesStEnvelope()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01],
                spatialRelationship: SpatialRelationship.EnvelopeIntersects,
                srid: 4326)
        };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("ST_Intersects(ST_Envelope(\"geom\"), ST_Envelope(ST_GeomFromWKB(@p0, 4326)))", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    // Esri esriSpatialRelWithin/Contains: filter geometry is within/contains the feature, so the
    // filter geometry must be the FIRST operand (ST_Within(filter, feature)). Disjoint is
    // symmetric. Reversing Within/Contains inverts the relationship and returns the wrong
    // (empty) set (#2068).
    [InlineData(SpatialRelationship.Within, "ST_Within(ST_GeomFromWKB(@p0, 4326), \"geom\")")]
    [InlineData(SpatialRelationship.Contains, "ST_Contains(ST_GeomFromWKB(@p0, 4326), \"geom\")")]
    [InlineData(SpatialRelationship.Disjoint, "ST_Disjoint(\"geom\", ST_GeomFromWKB(@p0, 4326))")]
    public void BuildSelectQuery_SpatialRelationships_EmitNativeFunctions(SpatialRelationship relationship, string expected)
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], relationship, srid: 4326)
        };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains(expected, result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_CrossSridFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Intersects, srid: 3857)
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
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

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
        Assert.Contains("nearest-neighbor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSelectQuery_DistanceFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.CreateDistanceFilter([0x01], distance: 100, srid: 4326)
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
        Assert.Contains("distance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSelectQuery_TemporalFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime
            }
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_OutputSrid_ThrowsNotSupported()
    {
        var query = new FeatureQuery { OutputSrid = 3857 };

        var ex = Assert.Throws<NotSupportedException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
        Assert.Contains("Output SRID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithOrderBy_QuotesFields()
    {
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(new OrderByClause("name", ascending: true))
        };

        var result = RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes);

        Assert.Contains("ORDER BY \"name\" ASC", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_GeneratesCountStar()
    {
        var result = RedshiftFeatureQueryBuilder.BuildCountQuery(Mapping(), new FeatureQuery());

        Assert.Contains("SELECT COUNT(*) FROM \"honua\".\"parcels\" WHERE 1=1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_SelectsPrimaryKeyOnly()
    {
        var result = RedshiftFeatureQueryBuilder.BuildObjectIdsQuery(Mapping(), new FeatureQuery());

        Assert.Contains("SELECT \"id\" AS \"__objectid\" FROM \"honua\".\"parcels\"", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_AsBinary", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_GeometryLayer_UsesBoundingBoxAccessors()
    {
        var result = RedshiftFeatureQueryBuilder.BuildExtentQuery(Mapping(), new FeatureQuery());

        Assert.Contains("MIN(ST_XMin(\"geom\"))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MIN(ST_YMin(\"geom\"))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_XMax(\"geom\"))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_YMax(\"geom\"))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("\"geom\" IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_GeographyLayer_CastsToGeometry()
    {
        var result = RedshiftFeatureQueryBuilder.BuildExtentQuery(
            Mapping(geometryType: RedshiftGeometryColumnType.Geography), new FeatureQuery());

        Assert.Contains("ST_XMin(ST_GeomFromWKB(ST_AsBinary(\"geom\")))", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_NoGeometryColumn_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RedshiftFeatureQueryBuilder.BuildExtentQuery(Mapping(geometryColumn: null), new FeatureQuery()));
        Assert.Contains("no geometry column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSelectQuery_NullMapping_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(null!, new FeatureQuery(), _attributes));
    }

    [Fact]
    public void BuildSelectQuery_InvalidWhereSyntax_ThrowsArgumentException()
    {
        var query = new FeatureQuery { Where = "totally invalid (((" };

        Assert.Throws<ArgumentException>(
            () => RedshiftFeatureQueryBuilder.BuildSelectQuery(Mapping(), query, _attributes));
    }
}
