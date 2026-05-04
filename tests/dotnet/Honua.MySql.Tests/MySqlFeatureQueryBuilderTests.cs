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

        // Default flavor is Mysql, so the axis-order=long-lat option is emitted on ST_AsWKB to
        // force canonical X/Y WKB on geographic SRSes regardless of the SRS-defined axis order.
        Assert.Contains("SELECT `id`, ST_AsWKB(`geom`, 'axis-order=long-lat')", result.Sql, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("start_and_end = 1")]   // 'and' embedded between underscores
    [InlineData("and_flag = 1")]         // 'and' as a leading token
    [InlineData("end_and = 1")]          // 'and' as a trailing token
    public void BuildSelectQuery_WithUnderscoreIdentifierContainingAnd_ParsesAsSingleField(string whereClause)
    {
        // The WHERE splitter treats AND as a logical operator only when adjacent characters
        // are not part of an identifier. Underscore is a legal MySQL identifier character so
        // it must NOT split tokens — otherwise valid columns like start_and_end or and_flag
        // get rejected as malformed expressions before SQL generation.
        // We register the column on the layer mapping so the splitter has a concrete identifier
        // to recover; the SQL itself just needs a single AND-joined clause for the comparison.
        var fieldName = whereClause.Split(' ')[0];
        var mapping = new MySqlLayerMapping
        {
            LayerId = 200,
            TableName = "events",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = [fieldName],
            GeometryType = GeometryType.Point
        };
        var builder = new MySqlFeatureQueryBuilder(new MySqlLayerMappingRegistry([mapping]));

        var query = new FeatureQuery { Where = whereClause };
        var result = builder.BuildSelectQuery(200, query);

        Assert.Contains($"AND (`{fieldName}` = @p0)", result.Sql, StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
    }

    [Fact]
    public void BuildSelectQuery_WhereClauseChainingMultipleUnderscoreFields_SplitsOnLogicalAndOnly()
    {
        // Combine an embedded-and identifier with a logical AND to confirm the splitter
        // distinguishes them: only the standalone AND should separate the comparisons.
        var mapping = new MySqlLayerMapping
        {
            LayerId = 201,
            TableName = "events",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["start_and_end", "name"],
            GeometryType = GeometryType.Point
        };
        var builder = new MySqlFeatureQueryBuilder(new MySqlLayerMappingRegistry([mapping]));

        var query = new FeatureQuery { Where = "start_and_end = 1 AND name = 'x'" };
        var result = builder.BuildSelectQuery(201, query);

        Assert.Contains("`start_and_end` = @p0", result.Sql, StringComparison.Ordinal);
        Assert.Contains("`name` = @p1", result.Sql, StringComparison.Ordinal);
        Assert.Equal(2, result.WhereParameters.Count);
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

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat'))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_Intersects(`geom`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat'))", result.Sql, StringComparison.Ordinal);
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

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat'))", result.Sql, StringComparison.Ordinal);
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
    public void BuildSelectQuery_TemporalFilter_ThrowsNotSupported()
    {
        // datetime / temporal predicates from OGC API Features, STAC, OData-time arrive
        // on FeatureQuery.TemporalFilter. The slice does not generate temporal SQL, so
        // surface NotSupportedException eagerly to avoid silently dropping the filter.
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                End = DateTimeOffset.Parse("2024-12-31T23:59:59Z", System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildSelectQuery(LayerId, query));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_TemporalFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildCountQuery(LayerId, query));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_TemporalFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.Date,
                End = DateTimeOffset.Parse("2024-12-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildExtentQuery(LayerId, query));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_TemporalFilter_ThrowsNotSupported()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = DateTimeOffset.Parse("2024-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        var ex = Assert.Throws<NotSupportedException>(() => _builder.BuildObjectIdsQuery(LayerId, query));
        Assert.Contains("Temporal filters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCountQuery_GeneratesCorrectSql()
    {
        var result = _builder.BuildCountQuery(LayerId, new FeatureQuery());

        Assert.Contains("SELECT COUNT(*) FROM `honua`.`parcels` WHERE 1=1", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_PolygonLayer_UsesEnvelopeWithoutTwoArgStSrid()
    {
        // Default test layer is Polygon; envelope corners come via ST_PointN(ST_ExteriorRing(...)).
        // ST_SRID(geom, 0) (the 2-arg setter) is MySQL-only and breaks MariaDB, so the slice
        // applies envelope/PointN directly to the column without retagging.
        var result = _builder.BuildExtentQuery(LayerId, new FeatureQuery());

        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(`geom`)), 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(`geom`)), 3)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_SRID(`geom`, 0)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `honua`.`parcels`", result.Sql, StringComparison.Ordinal);
        Assert.Contains("`geom` IS NOT NULL", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtentQuery_MultiPolygonLayer_UsesEnvelopePattern()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = 42,
            TableName = "regions",
            GeometryColumn = "shape",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.MultiPolygon
        };
        var builder = new MySqlFeatureQueryBuilder(new MySqlLayerMappingRegistry([mapping]));

        var result = builder.BuildExtentQuery(42, new FeatureQuery());

        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(`shape`)), 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ST_PointN(ST_ExteriorRing(ST_Envelope(`shape`)), 3)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_SRID(`shape`, 0)", result.Sql, StringComparison.Ordinal);
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

        Assert.Contains("MIN(ST_X(`loc`))", result.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(ST_Y(`loc`))", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_SRID(`loc`, 0)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ST_Envelope", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GeometryType.MultiPoint)]
    [InlineData(GeometryType.LineString)]
    [InlineData(GeometryType.MultiLineString)]
    [InlineData(GeometryType.GeometryCollection)]
    [InlineData(GeometryType.None)]
    public void BuildExtentQuery_UnsupportedGeometryType_ThrowsNotSupported(GeometryType geometryType)
    {
        // ST_X is point-only and ST_Envelope of a degenerate (vertical/horizontal) line
        // returns a LineString — both break the per-row extent pattern. The slice rejects
        // these geometry types instead of emitting SQL that fails at runtime on MySQL or
        // produces NULL extents on MariaDB.
        var mapping = new MySqlLayerMapping
        {
            LayerId = 7,
            TableName = "items",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = geometryType
        };
        var builder = new MySqlFeatureQueryBuilder(new MySqlLayerMappingRegistry([mapping]));

        var ex = Assert.Throws<NotSupportedException>(() => builder.BuildExtentQuery(7, new FeatureQuery()));
        Assert.Contains(geometryType.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Point, Polygon, MultiPolygon", ex.Message, StringComparison.Ordinal);
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

        Assert.Contains("MBRIntersects(`geom`, ST_GeomFromWKB(@p0, 4326, 'axis-order=long-lat'))", result.Sql, StringComparison.Ordinal);
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

    [Fact]
    public void BuildSelectQuery_MariaDbFlavor_OmitsAxisOrderOptionOnAsWkb()
    {
        // MariaDB does not support the third axis-order argument on ST_AsWKB / ST_GeomFromWKB.
        // It also does not enforce SRS-based axis order, so WKB-natural X/Y matches the
        // canonical Honua contract without the option. Emitting the 3-arg form on MariaDB
        // would fail at runtime, so the slice gates the option behind the engine flavor.
        var mapping = new MySqlLayerMapping
        {
            LayerId = 11,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.Polygon
        };
        var builder = new MySqlFeatureQueryBuilder(
            new MySqlLayerMappingRegistry([mapping]),
            MySqlEngineFlavor.MariaDb);

        var result = builder.BuildSelectQuery(11, new FeatureQuery());

        Assert.Contains("ST_AsWKB(`geom`)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("axis-order", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithPaginationAndNoOrderBy_InjectsPrimaryKeyOrder()
    {
        // SQL row order is unspecified without ORDER BY; LIMIT/OFFSET on top of an unordered
        // result set lets pages repeat or skip rows. The builder injects an ascending sort on
        // the configured primary key column whenever LIMIT or OFFSET is set without a
        // caller-supplied OrderBy. This covers BuildSelectQuery, BuildObjectIdsQuery, and
        // every store path that flows through them (QueryAsync, QueryPageAsync, streaming).
        var query = new FeatureQuery { Limit = 10 };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("ORDER BY `id` ASC LIMIT @p0", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithOffsetOnly_EmitsSentinelLimitBeforeOffset()
    {
        // MySQL pagination syntax requires LIMIT before OFFSET — bare OFFSET is a syntax
        // error. OData $skip without $top maps to Offset without Limit, so the builder must
        // emit a valid statement on this shape. Per the MySQL reference, the canonical "skip
        // N, take all remaining" idiom is `LIMIT 18446744073709551615 OFFSET N`.
        var query = new FeatureQuery { Offset = 50 };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains(
            "ORDER BY `id` ASC LIMIT 18446744073709551615 OFFSET @p0",
            result.Sql,
            StringComparison.Ordinal);
        Assert.Single(result.WhereParameters);
        Assert.Equal(50, result.WhereParameters[0]);
    }

    [Fact]
    public void BuildSelectQuery_NoPaginationAndNoOrderBy_OmitsOrderBy()
    {
        // Unpaginated reads materialise the entire result set; the caller is responsible for
        // any stable ordering. Don't inject ORDER BY here — it would force a sort the caller
        // didn't ask for and add cost on hot non-paginated paths.
        var result = _builder.BuildSelectQuery(LayerId, new FeatureQuery());

        Assert.DoesNotContain("ORDER BY", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_WithLimitAndOffset_EmitsLimitBeforeOffset()
    {
        // Combined LIMIT and OFFSET is the canonical paged-read shape; verify the parameter
        // order so the regression that introduced the bare-OFFSET path (now using a sentinel
        // LIMIT for offset-only) cannot reappear.
        var query = new FeatureQuery { Limit = 10, Offset = 20 };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains(
            "LIMIT @p0 OFFSET @p1",
            result.Sql,
            StringComparison.Ordinal);
        Assert.Equal(2, result.WhereParameters.Count);
        Assert.Equal(10, result.WhereParameters[0]);
        Assert.Equal(20, result.WhereParameters[1]);
    }

    [Fact]
    public void BuildSelectQuery_WithCallerOrderBy_DoesNotAppendPrimaryKey()
    {
        // Caller-supplied OrderBy is honored as-is — appending the primary key would change
        // tie-breaking semantics the caller didn't ask for.
        var query = new FeatureQuery
        {
            Limit = 10,
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name"))
        };

        var result = _builder.BuildSelectQuery(LayerId, query);

        Assert.Contains("ORDER BY `name` ASC", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`id` ASC", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIdsQuery_WithPaginationAndNoOrderBy_InjectsPrimaryKeyOrder()
    {
        // BuildObjectIdsQuery shares the OrderBy/Pagination append helpers, so the same
        // deterministic-order rule applies — paginated object-id reads must be reproducible
        // across calls.
        var query = new FeatureQuery { Limit = 5 };

        var result = _builder.BuildObjectIdsQuery(LayerId, query);

        Assert.Contains("ORDER BY `id` ASC LIMIT @p0", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_MariaDbFlavor_OmitsAxisOrderOptionOnSpatialFilter()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = 12,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.Polygon
        };
        var builder = new MySqlFeatureQueryBuilder(
            new MySqlLayerMappingRegistry([mapping]),
            MySqlEngineFlavor.MariaDb);

        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [0x01],
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 4326)
        };

        var result = builder.BuildSelectQuery(12, query);

        Assert.Contains("ST_GeomFromWKB(@p0, 4326)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("axis-order", result.Sql, StringComparison.Ordinal);
    }
}
