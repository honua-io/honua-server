// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using Honua.Core.Exceptions;
using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Microsoft.Extensions.ObjectPool;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Integration coverage for FlatGeobuf / Geobuf output on source-backed (provider-routed)
/// PostGIS layers via <see cref="PostgresStorageMappedFeatureReader"/> (honua-server#1938).
/// Before the fix the storage-mapped reader threw <see cref="NotSupportedException"/> for
/// <c>f=fgb</c> and lacked <see cref="IGeobufFeatureStore"/> entirely, so the FeatureServer
/// emitted a 400 for every compat-seeded layer. These tests run the encoders against a real
/// PostGIS table to prove both formats now produce valid payloads over the storage mapping.
/// </summary>
[Collection("Database")]
public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests : IAsyncLifetime
{
    private static readonly ObjectPool<Dictionary<string, object?>> DictionaryPool =
        new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());

    private readonly PostgresFixture _fixture;
    private string _schema = null!;

    public PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests));

        // A source-backed table is the standard provider-routed shape: an arbitrary physical
        // table with a primary key, a geometry column, and column-per-field attributes (no
        // shared 'layer_id'/'attributes' jsonb columns). The reader maps reads onto it.
        await _fixture.ExecuteAsync($"""
            CREATE TABLE {_schema}.cities (
                objectid bigint PRIMARY KEY,
                geom geometry(Point, 4326),
                name text,
                population integer,
                attributes jsonb
            );

            INSERT INTO {_schema}.cities (objectid, geom, name, population, attributes) VALUES
                (1, ST_SetSRID(ST_MakePoint(-157.8583, 21.3069), 4326), 'Honolulu', 350000, jsonb_build_object('eo:cloud_cover', 12.5)),
                (2, ST_SetSRID(ST_MakePoint(-156.3319, 20.7984), 4326), 'Kahului', 26000, jsonb_build_object('eo:cloud_cover', 3.0));
            """);
    }

    public Task DisposeAsync() => _fixture.DropSchemaAsync(_schema);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPageAsync_NativeDecimalPublishedAsDouble_PreservesDeclaredPrecision(bool smallintHint)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN reading numeric;
            UPDATE {_schema}.cities SET reading = 0.99999999999999999999 WHERE objectid = 1;
            UPDATE {_schema}.cities SET reading = 2 WHERE objectid = 2;
            """);
        var reader = CreateReader(includeDecimalField: true, smallintHint: smallintHint);
        var result = await reader.QueryPageAsync(1, new FeatureQuery
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'reading', '')::double precision = @p0", [1])
        });

        result.Items.Select(feature => feature.Id).Should().Equal(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPageAsync_NumericFilter_PreservesPhysicalAndJsonbNullSemantics(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities SET attributes = jsonb_build_object('population', population);
            INSERT INTO {_schema}.cities (objectid, population, attributes) VALUES
                (3, NULL, jsonb_build_object('population', '')),
                (4, 40000, jsonb_build_object('population', '40000')),
                (5, NULL, jsonb_build_object('population', NULL));
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null);
        var range = await reader.QueryPageAsync(1, new FeatureQuery
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer >= @p0", [30000])
        });
        range.Items.Select(feature => feature.Id).Should().Equal(1, 4);
        var nulls = await reader.QueryPageAsync(1, new FeatureQuery
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer IS NULL", [])
        });
        nulls.Items.Select(feature => feature.Id).Should().Equal(3, 5);
    }

    [Fact]
    public async Task QueryPageAsync_CanonicalNumericFilter_UsesSourceIndexAndPreservesResults()
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.cities (objectid, population)
                SELECT i + 10, i FROM generate_series(1, 100000) AS i;
            INSERT INTO {_schema}.cities (objectid, population) VALUES (100011, NULL);
            CREATE INDEX cities_population_perf_idx ON {_schema}.cities (population);
            ANALYZE {_schema}.cities;
            """);
        var reader = CreateReader();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer >= @p0", [350000])
        };
        var result = await reader.QueryPageAsync(1, query);
        result.Items.Select(feature => feature.Id).Should().Equal(1);

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query, false])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        var plan = (string)(await command.ExecuteScalarAsync())!;
        plan.Should().Contain("cities_population_perf_idx",
            "a selective predicate on a typed source column must retain index eligibility");
    }

    [Fact]
    public async Task QueryPageAsync_SmallintPublishedAsInteger_UsesDeclaredTypeExpressionIndex()
    {
        await _fixture.ExecuteAsync($"""
            TRUNCATE {_schema}.cities;
            ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE smallint;
            INSERT INTO {_schema}.cities (objectid, population)
                SELECT i, (i % 30000)::smallint FROM generate_series(1, 100000) AS i;
            INSERT INTO {_schema}.cities (objectid, population) VALUES
                (100001, -32768), (100002, 32767), (100003, NULL);
            CREATE INDEX cities_population_smallint_idx ON {_schema}.cities (population);
            CREATE INDEX cities_population_integer_expr_idx ON {_schema}.cities ((population::integer));
            ANALYZE {_schema}.cities;
            """);
        var reader = CreateReader();
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer = @p0", [32767])
        };
        var result = await reader.QueryPageAsync(1, query);
        result.Items.Select(feature => feature.Id).Should().Equal(100002);

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query, false])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        var plan = (string)(await command.ExecuteScalarAsync())!;
        plan.Should().Contain("cities_population_integer_expr_idx",
            "the expression index must serve the declared Integer predicate without removing its widening cast");

        var outsideSmallintRange = await reader.QueryPageAsync(1, query with
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer = @p0", [40000])
        });
        outsideSmallintRange.Items.Should().BeEmpty();

        var widenedArithmetic = await reader.QueryPageAsync(1, query with
        {
            SqlFilter = new SqlFragment("(NULLIF(\"attributes\" ->> 'population', '')::integer + NULLIF(\"attributes\" ->> 'population', '')::integer) = @p0", [65534])
        });
        widenedArithmetic.Items.Select(feature => feature.Id).Should().Equal(100002);

        var lowerBoundary = await reader.QueryPageAsync(1, query with
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer <= @p0", [-32768])
        });
        lowerBoundary.Items.Select(feature => feature.Id).Should().Equal(100001);

        var nulls = await reader.QueryPageAsync(1, query with
        {
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer IS NULL", [])
        });
        nulls.Items.Select(feature => feature.Id).Should().Equal(100003);
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_SourceBackedLayer_ReturnsFlatGeobufPayload()
    {
        var reader = CreateReader();

        reader.Should().BeAssignableTo<IFlatGeobufFeatureStore>(
            "the source-backed reader must advertise the FlatGeobuf marker so the FeatureServer gates f=fgb on the underlying table support");

        var payload = await reader.QueryFlatGeobufAsync(layerId: 1, new FeatureQuery(), CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Length.Should().BeGreaterThan(0);
        // FlatGeobuf magic bytes begin with ASCII "fgb" (0x66 0x67 0x62).
        payload[0].Should().Be(0x66);
        payload[1].Should().Be(0x67);
        payload[2].Should().Be(0x62);
    }

    [Fact]
    public async Task QueryGeobufAsync_SourceBackedLayer_ReturnsGeobufPayload()
    {
        var reader = CreateReader();

        var geobufStore = reader.Should().BeAssignableTo<IGeobufFeatureStore>().Subject;

        var payload = await geobufStore.QueryGeobufAsync(layerId: 1, new FeatureQuery(), CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_AttributesDocumentWithNamespacedField_ReturnsPayload()
    {
        var reader = CreateReader(attributesColumn: "attributes", includeNamespacedField: true);

        var payload = await reader.QueryFlatGeobufAsync(layerId: 1, new FeatureQuery(), CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_NoMatchingRows_ReturnsNull()
    {
        var reader = CreateReader();

        var payload = await reader.QueryFlatGeobufAsync(
            layerId: 1,
            new FeatureQuery { Where = "population > 100000000" },
            CancellationToken.None);

        payload.Should().BeNull();
    }

    [Fact]
    public async Task GetMvtTileAsync_SourceBackedLayer_UsesMappedTable()
    {
        var tileProvider = CreateReader().Should().BeAssignableTo<ITileProvider>().Subject;

        var payload = await tileProvider.GetMvtTileAsync(
            layerId: 1,
            x: 0,
            y: 0,
            z: 0,
            query: new FeatureQuery
            {
                SpatialReferenceSrid = 4326,
                OutputSrid = 4326
            },
            tileOptions: new TileOptions { TileBuffer = 0, TileExtent = 4096 },
            tileLimits: new TileLimits { MaxFeaturesPerTile = 100 },
            cancellationToken: CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Should().NotBeEmpty();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMvtTileAsync_TypedAttributes_PreservesTypesNullsAndFieldMask(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities ADD COLUMN rating double precision, ADD COLUMN active boolean;
            UPDATE {_schema}.cities SET rating = 12.5, active = true WHERE objectid = 1;
            UPDATE {_schema}.cities SET name = NULL, population = NULL, active = false WHERE objectid = 2;
            UPDATE {_schema}.cities SET attributes = jsonb_build_object(
                'objectid', -1, 'name', name, 'population', population, 'rating', rating, 'active', active);
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null,
            additionalFields:
            [
                new MetadataV2Field { Name = "rating", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "active", Type = MetadataV2FieldType.Boolean }
            ]);
        var query = new FeatureQuery { OutputSrid = 4326 };
        var options = new TileOptions { TileBuffer = 0, TileExtent = 4096 };
        var payload = await reader.GetMvtTileAsync(1, 0, 0, 0, query, options, new TileLimits());
        payload.Should().NotBeNullOrEmpty();
        var features = MvtTileDecoder.Decode(payload!).Layer("layer").Features;
        features.Should().HaveCount(2);
        var first = features.Single(feature => Convert.ToInt64(feature.Attributes["objectid"], CultureInfo.InvariantCulture) == 1);
        first.Attributes["name"].Should().Be("Honolulu");
        first.Attributes["population"].Should().BeOfType<ulong>().Which.Should().Be(350000);
        first.Attributes["rating"].Should().BeOfType<double>().Which.Should().Be(12.5);
        first.Attributes["active"].Should().Be(true);
        var second = features.Single(feature => Convert.ToInt64(feature.Attributes["objectid"], CultureInfo.InvariantCulture) == 2);
        second.Attributes.Keys.Should().BeEquivalentTo(["objectid", "active"]);
        second.Attributes["active"].Should().Be(false);

        var projected = await reader.GetMvtTileAsync(1, 0, 0, 0, query with
        {
            OutFields = ["name", "population", "active"],
            EnforcedMaskedFields = ["POPULATION"]
        }, options, new TileLimits());
        var projectedFeatures = MvtTileDecoder.Decode(projected!).Layer("layer").Features;
        projectedFeatures.Should().HaveCount(2);
        projectedFeatures.Should().OnlyContain(feature =>
            !feature.Attributes.ContainsKey("population") && !feature.Attributes.ContainsKey("rating"));
        projectedFeatures.Select(feature => Convert.ToInt64(feature.Attributes["objectid"], CultureInfo.InvariantCulture))
            .Should().BeEquivalentTo([1L, 2L]);

        foreach (var emptyQuery in new[] { query with { ExcludeAttributes = true }, query with { OutFields = [] } })
        {
            var empty = await reader.GetMvtTileAsync(1, 0, 0, 0, emptyQuery, options, new TileLimits());
            var emptyFeatures = MvtTileDecoder.Decode(empty!).Layer("layer").Features;
            emptyFeatures.Should().HaveCount(2);
            emptyFeatures.Should().OnlyContain(
                feature => feature.Attributes.Count == 1 && feature.Attributes.ContainsKey("objectid"));
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMvtTileAsync_WideAttributes_PreservesEveryChunk(bool useAttributes)
    {
        var fields = Enumerable.Range(1, 51).Select(index => new MetadataV2Field
        {
            Name = $"field_{index}",
            Type = MetadataV2FieldType.Integer
        }).ToArray();
        var columns = string.Join(", ", fields.Select(field => $"ADD COLUMN {field.Name} integer DEFAULT 7"));
        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.cities {columns};
            UPDATE {_schema}.cities AS city SET attributes = to_jsonb(city) - 'geom' - 'attributes';
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null, additionalFields: fields);
        var query = new FeatureQuery { OutputSrid = 4326, OutFields = [.. fields.Select(field => field.Name)] };
        var page = await reader.QueryPageAsync(1, query);
        page.Items.Should().HaveCount(2);
        foreach (var feature in page.Items)
        {
            feature.Attributes.Should().HaveCount(52, "feature reads also include the canonical object ID");
            feature.Attributes.Should().ContainKeys(fields.Select(field => field.Name));
        }
        var payload = await reader.GetMvtTileAsync(1, 0, 0, 0, query,
            new TileOptions { TileBuffer = 0 }, new TileLimits());
        var features = MvtTileDecoder.Decode(payload!).Layer("layer").Features;
        features.Should().HaveCount(2);
        foreach (var feature in features)
        {
            feature.Attributes.Should().HaveCount(52);
            foreach (var field in fields)
            {
                feature.Attributes[field.Name].Should().BeOfType<ulong>().Which.Should().Be(7);
            }
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPageAsync_DistinctAttributes_PreservesTextReaderBoundary(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities SET name = 'same', attributes = jsonb_build_object('name', 'same');
            """);
        var result = await CreateReader(attributesColumn: useAttributes ? "attributes" : null)
            .QueryPageAsync(1, new FeatureQuery { Distinct = true, OutFields = ["name"] });
        result.Items.Should().ContainSingle().Which.Attributes["name"].Should().Be("same");
    }

    [Fact]
    public async Task GetMvtTileAsync_SourceBackedLayer_EnforcesEncodedByteBudget()
    {
        var provider = CreateReader();
        var query = new FeatureQuery { SpatialReferenceSrid = 4326, OutputSrid = 4326 };
        var options = new TileOptions { TileBuffer = 0, TileExtent = 4096 };
        var payload = await provider.GetMvtTileAsync(1, 0, 0, 0, query, options, new TileLimits());
        payload.Should().NotBeNullOrEmpty();

        var exact = await provider.GetMvtTileAsync(1, 0, 0, 0, query, options,
            new TileLimits { MaxTileSize = payload!.Length });
        exact.Should().Equal(payload);
        await Assert.ThrowsAsync<TileSizeLimitExceededException>(() =>
            provider.GetMvtTileAsync(1, 0, 0, 0, query, options,
                new TileLimits { MaxTileSize = payload.Length - 1 }));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task QueryStatisticsAsync_OrdersGroupedFieldsWithJsonbAndPhysicalStorage(
        bool useAttributes, bool ascending)
    {
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.cities (objectid, name, population) VALUES
                (3, 'Honolulu', 10), (4, NULL, 5);
            UPDATE {_schema}.cities SET attributes =
                jsonb_build_object('name', name, 'population', population);
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null);
        var query = new FeatureQuery
        {
            GroupByFields = ["name"],
            OutStatistics =
            [
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "feature_count"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "population",
                    OutStatisticFieldName = "total_population"
                }
            ],
            Having = [new HavingCondition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "objectid",
                Operator = HavingComparisonOperator.GreaterThan,
                Value = 0
            }],
            OrderBy = [new OrderByClause("NAME", ascending) { NullOrdering = NullOrdering.NullsLast }],
            Limit = 3,
            Offset = 0
        };

        var rows = await reader.QueryStatisticsAsync(1, query, CancellationToken.None);

        rows.Should().HaveCount(3);
        rows.Select(row => row["name"]).Should().Equal(
            ascending ? new object?[] { "Honolulu", "Kahului", null } : new object?[] { "Kahului", "Honolulu", null });
        var honolulu = rows.Single(row => Equals(row["name"], "Honolulu"));
        Convert.ToInt64(honolulu["feature_count"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(2);
        Convert.ToInt64(honolulu["total_population"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(350010);
        var nullGroup = rows[^1];
        Convert.ToInt64(nullGroup["feature_count"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToInt64(nullGroup["total_population"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryStatisticsAsync_OrdersSecondGroupFieldBeforeFirst(bool useAttributes)
    {
        await _fixture.ExecuteAsync($"""
            UPDATE {_schema}.cities SET attributes =
                jsonb_build_object('name', name, 'population', population);
            """);
        var reader = CreateReader(attributesColumn: useAttributes ? "attributes" : null);
        var query = new FeatureQuery
        {
            GroupByFields = ["name", "population"],
            OutStatistics = [new StatisticDefinition
            {
                StatisticType = StatisticType.Count,
                OnStatisticField = "objectid",
                OutStatisticFieldName = "feature_count"
            }],
            OrderBy = [new OrderByClause("POPULATION"), new OrderByClause("name", ascending: false)]
        };

        var rows = await reader.QueryStatisticsAsync(1, query, CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Select(row => row["name"]).Should().Equal("Kahului", "Honolulu");
        rows.Select(row => Convert.ToInt64(row["feature_count"], System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal(1L, 1L);
    }

    [Fact]
    public async Task QueryPageAsync_SmallintHint_UsesOrdinaryIndexWithoutNarrowingLiteral()
    {
        await _fixture.ExecuteAsync($"""
            TRUNCATE {_schema}.cities;
            ALTER TABLE {_schema}.cities ALTER COLUMN population TYPE smallint;
            INSERT INTO {_schema}.cities (objectid, population)
                SELECT i, (i % 30000)::smallint FROM generate_series(1, 100000) AS i;
            INSERT INTO {_schema}.cities (objectid, population) VALUES
                (100001, -32768), (100002, 32767), (100003, NULL);
            CREATE INDEX cities_population_smallint_idx ON {_schema}.cities (population);
            ANALYZE {_schema}.cities;
            """);
        var reader = CreateReader(smallintHint: true);
        var query = new FeatureQuery
        {
            Limit = 10,
            SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer = @p0", [32767L])
        };
        var result = await reader.QueryPageAsync(1, query);
        result.Items.Select(feature => feature.Id).Should().Equal(100002);

        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query, true])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        foreach (var value in parameters)
        {
            command.Parameters.AddWithValue(value ?? DBNull.Value);
        }

        var plan = (string)(await command.ExecuteScalarAsync())!;
        plan.Should().Contain("cities_population_smallint_idx");

        foreach (var literal in new[] { -32768L, 40000L, long.MaxValue })
        {
            var page = await reader.QueryPageAsync(1, query with
            {
                SqlFilter = new SqlFragment("NULLIF(\"attributes\" ->> 'population', '')::integer = @p0", [literal])
            });
            long[] expectedIds = literal == -32768L ? [100001L] : [];
            page.Items.Select(feature => feature.Id).Should().Equal(expectedIds);
        }
    }

    private PostgresStorageMappedFeatureReader CreateReader(
        string? attributesColumn = null,
        bool includeNamespacedField = false,
        bool includeDecimalField = false,
        MetadataV2Field[]? additionalFields = null,
        bool smallintHint = false,
        string? connectionString = null,
        bool preferSerialPlan = false,
        IAdoNetDatabaseConnectionProvider? connectionProvider = null)
    {
        var schemaFields = new List<MetadataV2Field>
        {
            new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
            new() { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] },
            new() { Name = "name", Type = MetadataV2FieldType.String },
            new() { Name = "population", Type = MetadataV2FieldType.Integer },
        };
        if (includeNamespacedField)
        {
            schemaFields.Add(new MetadataV2Field { Name = "eo:cloud_cover", Type = MetadataV2FieldType.Double });
        }
        if (includeDecimalField)
        {
            schemaFields.Add(new MetadataV2Field { Name = "reading", Type = MetadataV2FieldType.Double });
        }

        if (additionalFields is not null)
        {
            schemaFields.AddRange(additionalFields);
        }

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-cities", Name = "Cities" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom",
            },
            SchemaFields = schemaFields.ToArray(),
        };

        var mapping = new FeatureStorageMapping(
            TableName: "cities",
            SchemaName: _schema,
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            AttributesColumn: attributesColumn,
            ProviderOptions: smallintHint
                ? new Dictionary<string, string>
                {
                    [FeatureStorageMapping.SourceBackedOption] = "true",
                    ["postgresSmallintColumn:population"] = "true"
                }
                : null);

        return new PostgresStorageMappedFeatureReader(
            connectionProvider ?? new FixtureConnectionProvider(connectionString ?? _fixture.ConnectionString),
            DictionaryPool,
            resource,
            mapping,
            connection: null,
            connectionEncryptionService: null,
            preferSerialBoundedSpatialReads: preferSerialPlan);
    }

    private sealed class FixtureConnectionProvider : IAdoNetDatabaseConnectionProvider
    {
        private readonly string _connectionString;

        public FixtureConnectionProvider(string connectionString) => _connectionString = connectionString;

        public string GetConnectionString() => _connectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = (NpgsqlConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            return (connection, transaction);
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
