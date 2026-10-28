// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Redshift.Features.FeatureStore;
using Honua.Db.Redshift.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Honua.Db.Redshift.Tests;

/// <summary>
/// Gated PostGIS stand-in tests for the Redshift Npgsql data-access path.
/// </summary>
/// <remarks>
/// <para>There is no official Amazon Redshift Testcontainer image. Because Redshift speaks the
/// PostgreSQL wire protocol and the Redshift SQL emitted for non-spatial reads, COUNT, object-id
/// listings, and the extent (<c>ST_AsBinary</c>, <c>ST_XMin</c>/<c>ST_YMin</c>/<c>ST_XMax</c>/
/// <c>ST_YMax</c>) is also valid against a PostGIS-enabled PostgreSQL server, this suite uses a
/// PostGIS Testcontainer purely as a wire-compatible stand-in to exercise the Npgsql connection
/// factory and data-access materialization. It does NOT prove Redshift-specific spatial semantics
/// — that requires a real Redshift cluster.</para>
/// <para>The suite is doubly gated: the <c>Category=RedshiftStandIn</c> trait keeps it out of the default
/// PR run, and <c>HONUA_TEST_REDSHIFT=1</c> must be set so a stray category filter does not start
/// Docker on machines without it. To run: <c>HONUA_TEST_REDSHIFT=1 dotnet test --filter Category=RedshiftStandIn</c>.</para>
/// </remarks>
[Trait("Category", "RedshiftStandIn")]
[Trait("Evidence", "PostGISWireCompatibility")]
public sealed class RedshiftFeatureDataAccessIntegrationTests : IAsyncLifetime
{
    private const string TestRedshiftEnvVar = "HONUA_TEST_REDSHIFT";

    private PostgreSqlContainer _container = null!;
    private RedshiftFeatureDataAccess _dataAccess = null!;
    private RedshiftFeatureStore _store = null!;
    private RedshiftLayerMapping _mapping = null!;
    private static readonly string[] _attributes = ["name", "area", "type"];

    public async Task InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(TestRedshiftEnvVar), "1", StringComparison.Ordinal))
        {
            return;
        }

        _container = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:16-3.4")
            .WithDatabase("honua_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();

        await using (var conn = new Npgsql.NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using (var ext = conn.CreateCommand())
            {
                ext.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis";
                await ext.ExecuteNonQueryAsync();
            }

            await using (var create = conn.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE parcels (
                        id BIGINT PRIMARY KEY,
                        geom geometry(Point, 4326) NOT NULL,
                        name VARCHAR(64),
                        area DOUBLE PRECISION,
                        type VARCHAR(32)
                    )
                    """;
                await create.ExecuteNonQueryAsync();
            }

            for (var i = 1; i <= 10; i++)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO parcels (id, geom, name, area, type)
                    VALUES (@id, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), @name, @area, @type)
                    """;
                cmd.Parameters.AddWithValue("id", i);
                cmd.Parameters.AddWithValue("lon", -122.0 + i * 0.01);
                cmd.Parameters.AddWithValue("lat", 37.0 + i * 0.01);
                cmd.Parameters.AddWithValue("name", $"Parcel {i}");
                cmd.Parameters.AddWithValue("area", i * 100.5);
                cmd.Parameters.AddWithValue("type", i % 2 == 0 ? "residential" : "commercial");
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var options = Options.Create(new RedshiftOptions { ConnectionString = connectionString });
        var factory = new RedshiftConnectionFactory(options);
        _dataAccess = new RedshiftFeatureDataAccess(factory, options, NullLogger<RedshiftFeatureDataAccess>.Instance);
        _store = new RedshiftFeatureStore(_dataAccess);

        _mapping = new RedshiftLayerMapping
        {
            LayerId = 1,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            GeometryColumnType = RedshiftGeometryColumnType.Geometry
        };
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [RequiredEnvironmentFact(TestRedshiftEnvVar, "1", skipReason: "stand-in-not-enabled:HONUA_TEST_REDSHIFT")]
    public async Task Count_Select_ObjectIds_Extent_RoundTripOverNpgsql()
    {
        var query = new FeatureQuery();

        var count = await _dataAccess.ExecuteCountAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildCountQuery(_mapping, query), dataConnection: null, CancellationToken.None);
        Assert.Equal(10, count);

        var features = await _dataAccess.ExecuteSelectAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildSelectQuery(_mapping, query, _attributes), _attributes,
            dataConnection: null, CancellationToken.None);
        Assert.Equal(10, features.Length);
        Assert.NotNull(features[0].Geometry);
        Assert.True(features[0].Attributes.ContainsKey("name"));

        var ids = await _dataAccess.ExecuteObjectIdsAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildObjectIdsQuery(_mapping, query), dataConnection: null, CancellationToken.None);
        Assert.Equal(10, ids.Length);

        var extent = await _dataAccess.ExecuteExtentAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildExtentQuery(_mapping, query), dataConnection: null, CancellationToken.None);
        Assert.NotNull(extent);
        var extentValue = extent!.Value;
        Assert.Equal(4326, extentValue.SpatialReference);
        Assert.True(extentValue.MinX < extentValue.MaxX);
        Assert.True(extentValue.MinY < extentValue.MaxY);
    }

    [RequiredEnvironmentFact(TestRedshiftEnvVar, "1", skipReason: "stand-in-not-enabled:HONUA_TEST_REDSHIFT")]
    public async Task Select_WithWhereFilter_ReturnsSubset()
    {
        var query = new FeatureQuery { Where = "type = 'commercial'" };

        var count = await _dataAccess.ExecuteCountAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildCountQuery(_mapping, query), dataConnection: null, CancellationToken.None);

        Assert.True(count is > 0 and < 10);
    }

    [RequiredEnvironmentFact(TestRedshiftEnvVar, "1", skipReason: "stand-in-not-enabled:HONUA_TEST_REDSHIFT")]
    public async Task QueryStatistics_HavingOrderByLimitAndOffset_ReturnsThePagedGroup()
    {
        if (_store is null || _container is null)
        {
            throw new InvalidOperationException(
                "Redshift stand-in did not start. This test must execute the statistics query, not pass without a database.");
        }

        var reader = _store.CreateReaderForBinding(CreateStatisticsBinding(_store));
        var grouped = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "id",
                    OutStatisticFieldName = "n"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "area",
                    OutStatisticFieldName = "area_sum"
                }),
            GroupByFields = ImmutableArray.Create("type")
        };

        var havingRows = await reader.QueryStatisticsAsync(1, grouped with
        {
            Having = ImmutableArray.Create(new HavingCondition
            {
                StatisticType = StatisticType.Sum,
                OnStatisticField = "area",
                Operator = HavingComparisonOperator.GreaterThan,
                Value = 2600
            })
        });

        // Odds are commercial (sum 2512.5); evens are residential (sum 3015). The threshold
        // keeps only residential. Two rows would mean HAVING never reached the database.
        Assert.Single(havingRows);
        Assert.Equal("residential", havingRows[0]["type"]);
        Assert.Equal(5L, Convert.ToInt64(havingRows[0]["n"], CultureInfo.InvariantCulture));
        Assert.Equal(3015d, Convert.ToDouble(havingRows[0]["area_sum"], CultureInfo.InvariantCulture));

        var pagedRows = await reader.QueryStatisticsAsync(1, grouped with
        {
            OrderBy = ImmutableArray.Create(new OrderByClause("type", ascending: true)),
            Limit = 1,
            Offset = 1
        });

        // type ASC is commercial then residential. OFFSET 1 LIMIT 1 is the second group.
        Assert.Single(pagedRows);
        Assert.Equal("residential", pagedRows[0]["type"]);
        Assert.Equal(5L, Convert.ToInt64(pagedRows[0]["n"], CultureInfo.InvariantCulture));
    }

    private static FeatureProviderBinding CreateStatisticsBinding(RedshiftFeatureStore provider)
    {
        var connectionId = Guid.NewGuid();
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "Parcels" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-parcels"],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "id",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "geom",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                },
                new MetadataV2Field { Name = "type", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "area", Type = MetadataV2FieldType.Double }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "public.parcels",
            StorageLayerId = 1
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = "1", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "stand-in" },
                    Provider = DataProviderNames.Redshift
                }
            ],
            StorageBindings = [storageBinding],
            Services = [service],
            Publications = [publication]
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
        var resolvedBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");

        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            resolvedBinding,
            FeatureStorageMapping.FromMetadata(resource, resolvedBinding),
            1,
            provider,
            Connection: null);
    }
}

/// <summary>
/// Live sentinel tests that prove the configured connection reaches Amazon Redshift and executes
/// Redshift spatial functions. These tests are separate from the PostGIS wire stand-in evidence.
/// </summary>
[Trait("Category", "RedshiftLive")]
[Trait("Evidence", "RealWarehouse")]
public sealed class RedshiftLiveIntegrationTests
{
    internal const string ConnectionEnvVar = "HONUA_REDSHIFT_TEST_CONNECTION";

    [RequiredEnvironmentFact(ConnectionEnvVar, skipReason: "missing-credential:HONUA_REDSHIFT_TEST_CONNECTION")]
    public async Task SpatialRoundTrip_ExecutesOnAmazonRedshift()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar)!;
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT version()";
        var version = Assert.IsType<string>(await versionCommand.ExecuteScalarAsync());
        Assert.Contains("Redshift", version, StringComparison.OrdinalIgnoreCase);

        await using var spatialCommand = connection.CreateCommand();
        spatialCommand.CommandText = "SELECT ST_AsText(ST_GeomFromText('POINT(-122.335167 47.608013)', 4326))";
        var roundTrip = Assert.IsType<string>(await spatialCommand.ExecuteScalarAsync());
        Assert.Contains("POINT", roundTrip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-122.335167", roundTrip, StringComparison.Ordinal);
        Assert.Contains("47.608013", roundTrip, StringComparison.Ordinal);
    }
}
