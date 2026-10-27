// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksFeatureQueryBuilderTests
{
    private static readonly DatabricksFeatureQueryBuilder Builder = new();
    private static readonly byte[] PolygonWkb = [0x01, 0x03, 0x00, 0x00];

    private static SpatialFilter Envelope(SpatialRelationship relationship) => SpatialFilter.Create(
        geometry: [1, 2, 3, 4],
        spatialRelationship: relationship,
        srid: 4326,
        isSimpleEnvelope: true,
        allowEnvelopeOnly: true,
        envelopeMinX: -10,
        envelopeMinY: -5,
        envelopeMaxX: 10,
        envelopeMaxY: 5);

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

    private static SpatialFilter GeometryFilter(SpatialRelationship relationship, int? srid = 4326) => SpatialFilter.Create(
        geometry: PolygonWkb,
        spatialRelationship: relationship,
        srid: srid,
        isSimpleEnvelope: false);

    [Fact]
    public void BuildSelectQuery_TextSearch_BindsLiteralTermsInProviderDialect()
    {
        const string term = "Harbor%_'\\";
        var query = new FeatureQuery
        {
            TextSearch = new FeatureTextSearch(["name", "owner"],
                [new FeatureSearchTerm[] { new(term, false), new("closed", true) }]),
            Limit = 2,
            Offset = 1
        };
        var result = Builder.BuildSelect(Mapping("name", "owner"), query);
        Assert.Contains("INSTR(", result.Sql, StringComparison.Ordinal);
        Assert.Contains(" OR ", result.Sql, StringComparison.Ordinal);
        Assert.Contains("NOT ", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(term, result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("attributes->>", result.Sql, StringComparison.Ordinal);
        Assert.Equal(term, result.Parameters[0].Value);
        Assert.Equal(term, result.Parameters[1].Value);
        Assert.Equal("closed", result.Parameters[2].Value);
        Assert.Equal("closed", result.Parameters[3].Value);
    }
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
    public void BuildSelect_TranslatesWhereClause_ToParameterizedSparkSql()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "owner = 'acme'" });

        // The literal is parameterized (no inline 'acme'); the column is backtick-quoted.
        Assert.Contains("WHERE (`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'acme'", statement.Sql, StringComparison.Ordinal);
        Assert.Single(statement.Parameters);
        Assert.Equal("f0", statement.Parameters[0].Name);
        Assert.Equal("acme", statement.Parameters[0].Value);
    }

    [Fact]
    public void BuildSelect_TranslatesNumericComparison_WithTypedParameter()
    {
        var statement = Builder.BuildSelect(Mapping("name", "owner", "pop"), new FeatureQuery { Where = "pop >= 1000" });

        Assert.Contains("`pop` >= :f0", statement.Sql, StringComparison.Ordinal);
        Assert.Equal("1000", statement.Parameters[0].Value);
        Assert.Equal("BIGINT", statement.Parameters[0].Type);
    }

    [Fact]
    public void BuildSelect_TranslatesInList_Parameterized()
    {
        var statement = Builder.BuildSelect(Mapping("name", "owner", "code"), new FeatureQuery { Where = "code IN (1, 2, 3)" });

        Assert.Contains("`code` IN (:f0, :f1, :f2)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(3, statement.Parameters.Count);
    }

    [Fact]
    public void BuildSelect_TranslatesIsNull()
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "owner IS NULL" });

        Assert.Contains("`owner` IS NULL", statement.Sql, StringComparison.Ordinal);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void BuildSelect_WhereWithUnknownField_Throws()
    {
        // Unknown fields are a client validation error (ArgumentException -> HTTP 400),
        // distinct from capability rejections (NotSupportedException).
        Assert.Throws<UnknownFilterFieldException>(
            () => Builder.BuildSelect(Mapping(), new FeatureQuery { Where = "not_a_column = 1" }));
    }

    [Fact]
    public void BuildSelect_WhereParametersAndObjectIds_DoNotCollide()
    {
        var query = new FeatureQuery { Where = "owner = 'acme'", ObjectIds = [7L] };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains(":f0", statement.Sql, StringComparison.Ordinal);
        Assert.Contains(":oid0", statement.Sql, StringComparison.Ordinal);
        Assert.Equal(2, statement.Parameters.Count);
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

    [Theory]
    [InlineData("eo:cloud_cover")]
    [InlineData("owner.name")]
    [InlineData("cloud-cover")]
    public void BuildSelect_WithExtendedOrderByField_QuotesSingleIdentifier(string field)
    {
        var query = new FeatureQuery { OrderBy = [OrderByClause.Asc(field)] };

        var statement = Builder.BuildSelect(Mapping(field), query);

        Assert.Contains($"ORDER BY `{field}` ASC", statement.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name;DROP")]
    [InlineData("owner name")]
    [InlineData("name/name")]
    public void BuildSelect_WithUnsafeOrderByField_Throws(string field)
    {
        var query = new FeatureQuery { OrderBy = [OrderByClause.Asc(field)] };

        Assert.Throws<ArgumentException>(() => Builder.BuildSelect(Mapping(field), query));
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
    public void BuildSelect_EmptyOutFields_ProjectsNoAttributeColumns()
    {
        var defaults = Builder.BuildSelect(Mapping(), new FeatureQuery());
        Assert.Contains("`name`", defaults.Sql, StringComparison.Ordinal);
        Assert.Contains("`owner`", defaults.Sql, StringComparison.Ordinal);

        var unset = Builder.BuildSelect(Mapping(), new FeatureQuery { OutFields = default(ImmutableArray<string>) });
        Assert.Contains("`name`", unset.Sql, StringComparison.Ordinal);
        Assert.Contains("`owner`", unset.Sql, StringComparison.Ordinal);

        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { OutFields = ImmutableArray<string>.Empty });

        Assert.DoesNotContain("`name`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCount_EmitsCountStarWithFilter()
    {
        var statement = Builder.BuildCount(Mapping("name", "owner", "x"), new FeatureQuery { Where = "x > 1" });

        Assert.StartsWith("SELECT COUNT(*) AS __honua_count FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE (`x` > :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.Single(statement.Parameters);
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
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = Envelope(SpatialRelationship.Intersects) });

        Assert.Contains("st_intersects(`geom`, st_geomfromtext('POLYGON((", statement.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpatialRelationship.Contains, "st_contains(st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Within, "st_within(st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Disjoint, "st_disjoint(`geom`, st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Crosses, "st_crosses(`geom`, st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Touches, "st_touches(`geom`, st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Overlaps, "st_overlaps(`geom`, st_geomfromtext('POLYGON((")]
    [InlineData(SpatialRelationship.Equals, "st_equals(`geom`, st_geomfromtext('POLYGON((")]
    public void BuildSelect_EnvelopeSpatialFilter_UsesMappedRelationship(SpatialRelationship relationship, string expected)
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = Envelope(relationship) });

        Assert.Contains(expected, statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("st_intersects(", statement.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpatialRelationship.EnvelopeIntersects)]
    [InlineData(SpatialRelationship.WithinDistance)]
    [InlineData(SpatialRelationship.BeyondDistance)]
    [InlineData(SpatialRelationship.NearestNeighbor)]
    public void BuildSelect_UnsupportedEnvelopeRelationship_Throws(SpatialRelationship relationship)
    {
        var withCoordinates = new FeatureQuery { SpatialFilter = Envelope(relationship) };
        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), withCoordinates));

        var withoutCoordinates = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: [1, 2, 3, 4],
                spatialRelationship: relationship,
                srid: 4326,
                isSimpleEnvelope: true,
                allowEnvelopeOnly: true)
        };
        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), withoutCoordinates));
    }

    [Fact]
    public void BuildSelect_SupplementalSqlFilter_DoesNotEmitWhereThatIgnoresIt()
    {
        var query = new FeatureQuery
        {
            Where = "owner = 'acme'",
            SqlFilter = new SqlFragment("(attributes->>'owner') = @p0 AND observed_at > @p1", new object?[] { "acme", 1 }),
            SqlFilterSupplementsWhere = true
        };

        var ex = Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(Mapping(), query));
        Assert.Contains("supplemental", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSelect_WhereWithTranslatedSqlFilter_StillUsesWhere()
    {
        var query = new FeatureQuery
        {
            Where = "owner = 'acme'",
            SqlFilter = new SqlFragment("(attributes->>'owner') = @p0", new object?[] { "acme" })
        };

        var statement = Builder.BuildSelect(Mapping(), query);

        Assert.Contains("WHERE (`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("->>", statement.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpatialRelationship.Intersects, "st_intersects(`geom`, st_geomfromwkb(unhex(:geom0), 4326))")]
    [InlineData(SpatialRelationship.Contains, "st_contains(st_geomfromwkb(unhex(:geom0), 4326), `geom`)")]
    [InlineData(SpatialRelationship.Within, "st_within(st_geomfromwkb(unhex(:geom0), 4326), `geom`)")]
    [InlineData(SpatialRelationship.Disjoint, "st_disjoint(`geom`, st_geomfromwkb(unhex(:geom0), 4326))")]
    [InlineData(SpatialRelationship.EnvelopeIntersects, "st_intersects(st_envelope(`geom`), st_envelope(st_geomfromwkb(unhex(:geom0), 4326)))")]
    public void BuildSelect_NonEnvelopeSpatialFilter_EmitsBoundPredicate(SpatialRelationship relationship, string expected)
    {
        var statement = Builder.BuildSelect(Mapping(), new FeatureQuery { SpatialFilter = GeometryFilter(relationship) });

        Assert.Contains(expected, statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("01030000", statement.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(statement.Parameters);
        Assert.Equal("geom0", parameter.Name);
        Assert.Equal("01030000", parameter.Value);
        Assert.Equal("STRING", parameter.Type);
    }

    [Fact]
    public void BuildSelect_NonEnvelopeSpatialFilter_UnspecifiedSrid_UsesLayerSrid()
    {
        AssertUsesLayerSrid(null);
        AssertUsesLayerSrid(0);

        static void AssertUsesLayerSrid(int? srid)
        {
            var statement = Builder.BuildSelect(
                Mapping(),
                new FeatureQuery { SpatialFilter = GeometryFilter(SpatialRelationship.Intersects, srid) });

            Assert.Contains("st_geomfromwkb(unhex(:geom0), 4326)", statement.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildSelect_NonEnvelopeSpatialFilter_MatchingSrid_UsesThatSrid()
    {
        var mapping = new DatabricksLayerMapping
        {
            LayerId = 1,
            Table = "parcels",
            Catalog = "main",
            Schema = "gis",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 3857,
            GeometryType = GeometryType.Polygon,
            AttributeColumns = ["name", "owner"],
        };

        var statement = Builder.BuildSelect(
            mapping,
            new FeatureQuery { SpatialFilter = GeometryFilter(SpatialRelationship.Within, 3857) });

        Assert.Contains("st_within(st_geomfromwkb(unhex(:geom0), 3857), `geom`)", statement.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("point", 1u)]
    [InlineData("line", 2u)]
    [InlineData("polygon", 3u)]
    public void BuildSelect_GeoServicesEwkb_BindsIsoWkbWithoutEmbeddedSrid(string kind, uint geometryType)
    {
        var ewkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(GeoServicesGeometry(kind));
        var ewkbType = BinaryPrimitives.ReadUInt32LittleEndian(ewkb.AsSpan(1));
        Assert.NotEqual(0u, ewkbType & 0x20000000u);

        var statement = Builder.BuildSelect(
            Mapping(),
            new FeatureQuery
            {
                SpatialFilter = SpatialFilter.Create(ewkb, SpatialRelationship.Intersects, srid: 4326),
            });

        var parameter = Assert.Single(statement.Parameters);
        var bound = Convert.FromHexString(parameter.Value!);
        var boundType = BinaryPrimitives.ReadUInt32LittleEndian(bound.AsSpan(1));
        Assert.Equal(0u, boundType & 0x20000000u);
        Assert.Equal(geometryType, boundType);
        Assert.Equal(Convert.ToHexString(WkbSridNormalizer.RemoveEmbeddedSrid(ewkb)), parameter.Value);
        Assert.Contains("st_geomfromwkb(unhex(:geom0), 4326)", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSelect_GeoServicesEwkb_CrossSrid_StillThrows()
    {
        var ewkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(GeoServicesGeometry("polygon"));

        var ex = Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(
            Mapping(),
            new FeatureQuery
            {
                SpatialFilter = SpatialFilter.Create(ewkb, SpatialRelationship.Intersects, srid: 3857),
            }));

        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    private static GeoServicesGeometry GeoServicesGeometry(string kind)
    {
        var spatialReference = new GeoServicesSpatialReference { Wkid = 4326 };
        return kind switch
        {
            "point" => new GeoServicesGeometry { X = 1, Y = 2, SpatialReference = spatialReference },
            "line" => new GeoServicesGeometry
            {
                Paths = [[[0, 0], [1, 1]]],
                SpatialReference = spatialReference,
            },
            "polygon" => new GeoServicesGeometry
            {
                Rings =
                [
                    [
                        [0, 0],
                        [0, 1],
                        [1, 1],
                        [1, 0],
                        [0, 0],
                    ],
                ],
                SpatialReference = spatialReference,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    [Fact]
    public void BuildSelect_NonEnvelopeSpatialFilter_CrossSrid_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(
            Mapping(),
            new FeatureQuery { SpatialFilter = GeometryFilter(SpatialRelationship.Intersects, 3857) }));

        Assert.Contains("Cross-SRID", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpatialRelationship.WithinDistance)]
    [InlineData(SpatialRelationship.BeyondDistance)]
    [InlineData(SpatialRelationship.NearestNeighbor)]
    [InlineData(SpatialRelationship.Crosses)]
    [InlineData(SpatialRelationship.Touches)]
    [InlineData(SpatialRelationship.Overlaps)]
    [InlineData(SpatialRelationship.Equals)]
    public void BuildSelect_UnsupportedNonEnvelopeRelationship_Throws(SpatialRelationship relationship)
    {
        Assert.Throws<NotSupportedException>(() => Builder.BuildSelect(
            Mapping(),
            new FeatureQuery { SpatialFilter = GeometryFilter(relationship) }));
    }

    [Fact]
    public void BuildCount_NonEnvelopeSpatialFilter_BindsGeometryBesideWhere()
    {
        var statement = Builder.BuildCount(
            Mapping(),
            new FeatureQuery
            {
                Where = "owner = 'acme'",
                SpatialFilter = GeometryFilter(SpatialRelationship.Disjoint),
            });

        Assert.Contains("(`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("st_disjoint(`geom`, st_geomfromwkb(unhex(:geom1), 4326))", statement.Sql, StringComparison.Ordinal);
        Assert.Equal("acme", statement.Parameters[0].Value);
        Assert.Equal("01030000", statement.Parameters[1].Value);
        Assert.Equal("geom1", statement.Parameters[1].Name);
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

    [Fact]
    public void BuildStatistics_EmitsAggregateColumns()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "owner", OutStatisticFieldName = "owner_count" },
                new StatisticDefinition { StatisticType = StatisticType.Sum, OnStatisticField = "pop", OutStatisticFieldName = "pop_sum" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("COUNT(`owner`) AS `owner_count`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(`pop`) AS `pop_sum`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM `main`.`gis`.`parcels`", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_WithGroupBy_EmitsGroupByAndProjectsGroupColumns()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Avg, OnStatisticField = "pop", OutStatisticFieldName = "pop_avg" },
            ],
            GroupByFields = ["owner"],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("SELECT `owner`, AVG(`pop`) AS `pop_avg`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY `owner`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_StddevAndVar_UseSparkSampleFunctions()
    {
        var query = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Stddev, OnStatisticField = "pop", OutStatisticFieldName = "pop_sd" },
                new StatisticDefinition { StatisticType = StatisticType.Var, OnStatisticField = "pop", OutStatisticFieldName = "pop_var" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping("name", "owner", "pop"), query);

        Assert.Contains("STDDEV_SAMP(`pop`) AS `pop_sd`", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("VAR_SAMP(`pop`) AS `pop_var`", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatistics_FiltersByWhereClause()
    {
        var query = new FeatureQuery
        {
            Where = "owner = 'acme'",
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = "owner", OutStatisticFieldName = "n" },
            ],
        };

        var statement = Builder.BuildStatistics(Mapping(), query);

        Assert.Contains("WHERE (`owner` = :f0)", statement.Sql, StringComparison.Ordinal);
        Assert.Equal("acme", statement.Parameters[0].Value);
    }

    [Fact]
    public void BuildStatistics_NoStatistics_ReturnsEmptyResultQuery()
    {
        var statement = Builder.BuildStatistics(Mapping(), new FeatureQuery());

        Assert.Equal("SELECT 1 WHERE FALSE", statement.Sql);
        Assert.Empty(statement.Parameters);
    }
}
