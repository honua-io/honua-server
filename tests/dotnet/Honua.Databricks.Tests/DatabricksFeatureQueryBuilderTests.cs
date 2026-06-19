// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;

namespace Honua.Databricks.Tests;

public class DatabricksFeatureQueryBuilderTests
{
    private static readonly DatabricksFeatureQueryBuilder Builder = new();

    private static DatabricksLayerMapping Mapping(params string[] attributes) => new()
    {
        LayerId = 1,
        Table = "parcels",
        Catalog = "main",
        Schema = "gis",
        GeometryColumn = "geom",
        PrimaryKeyColumn = "id",
        Srid = 4326,
        GeometryType = GeometryType.Polygon,
        AttributeColumns = attributes.Length == 0 ? ["name", "owner"] : attributes,
    };

    [Fact]
    public void BuildSelect_ProjectsIdGeometryHexAndAttributes_WithQualifiedTable()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery());

        Assert.Contains("`id` AS __honua_id", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("hex(st_asbinary(`geom`)) AS __honua_geom_hex", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("`owner`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void BuildSelect_ForwardsWhereClauseVerbatim()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "owner = 'acme'" });

        Assert.Contains("WHERE (owner = 'acme')", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_WithObjectIds_BindsNamedParameters()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { ObjectIds = [10L, 20L] });

        Assert.Contains("`id` IN (:oid0, :oid1)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(2, statement.Parameters.Count);
        Assert.Equal("oid0", statement.Parameters[0].Name);
        Assert.Equal("10", statement.Parameters[0].Value);
        Assert.Equal("BIGINT", statement.Parameters[0].Type);
        Assert.Equal("20", statement.Parameters[1].Value);
    }

    [Fact]
    public void BuildSelect_AppliesLimitOffsetAndOrderBy()
    {
        var query = new FeatureQuery
        {
            Limit = 50,
            Offset = 100,
            OrderBy = [OrderByClause.Asc("name"), OrderByClause.Desc("owner")],
        };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains("ORDER BY `name` ASC, `owner` DESC", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 50", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 100", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_WithOutFields_ProjectsOnlyRequestedConfiguredColumns()
    {
        var query = new FeatureQuery { OutFields = ["name"] };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(", `owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_ExcludeAttributes_ProjectsNoAttributeColumns()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { ExcludeAttributes = true });

        Assert.DoesNotContain("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCount_EmitsCountStarWithFilter()
    {
        var statement = Builder.BuildCount(Mapping(), new FeatureQuery { Where = "x > 1" });

        Assert.StartsWith("SELECT COUNT(*) AS __honua_count FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE (x > 1)", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtent_EmitsStMinMaxAggregates()
    {
        var statement = Builder.BuildExtent(Mapping(), null);

        Assert.Contains("MIN(st_xmin(`geom`))", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("MAX(st_ymax(`geom`))", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildObjectIds_SelectsPrimaryKeyOnly()
    {
        var statement = Builder.BuildObjectIds(Mapping(), new FeatureQuery());

        Assert.StartsWith("SELECT `id` AS __honua_id FROM", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("st_asbinary", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_EnvelopeSpatialFilter_EmitsStIntersects()
    {
        var spatial = SpatialFilter.Create(
            geometry: [1, 2, 3, 4],
            spatialRelationship: SpatialRelationship.Intersects,
            srid: 4326,
            isSimpleEnvelope: true,
            allowEnvelopeOnly: true,
            envelopeMinX: -10,
            envelopeMinY: -5,
            envelopeMaxX: 10,
            envelopeMaxY: 5);

        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = spatial });

        Assert.Contains("st_intersects(`geom`, st_geomfromtext('POLYGON((", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_NonEnvelopeSpatialFilter_Throws()
    {
        var spatial = SpatialFilter.Create(
            geometry: [1, 2, 3, 4],
            spatialRelationship: SpatialRelationship.Intersects,
            srid: 4326,
            isSimpleEnvelope: false);

        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = spatial }));
    }

    [Fact]
    public void BuildSelect_TemporalFilter_Throws()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter { PropertyName = "ts", PropertyType = TemporalPropertyType.DateTime },
        };

        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), query));
    }
}
