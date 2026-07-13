// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Redshift.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Honua.Redshift.Tests;

/// <summary>
/// Gated integration tests for the Redshift Npgsql data-access path.
/// </summary>
/// <remarks>
/// <para>There is no official Amazon Redshift Testcontainer image. Because Redshift speaks the
/// PostgreSQL wire protocol and the Redshift SQL emitted for non-spatial reads, COUNT, object-id
/// listings, and the extent (<c>ST_AsBinary</c>, <c>ST_XMin</c>/<c>ST_YMin</c>/<c>ST_XMax</c>/
/// <c>ST_YMax</c>) is also valid against a PostGIS-enabled PostgreSQL server, this suite uses a
/// PostGIS Testcontainer purely as a wire-compatible stand-in to exercise the Npgsql connection
/// factory and data-access materialization. It does NOT prove Redshift-specific spatial semantics
/// — that requires a real Redshift cluster.</para>
/// <para>The suite is doubly gated: the <c>Category=Redshift</c> trait keeps it out of the default
/// PR run, and <c>HONUA_TEST_REDSHIFT=1</c> must be set so a stray category filter does not start
/// Docker on machines without it. To run: <c>HONUA_TEST_REDSHIFT=1 dotnet test --filter Category=Redshift</c>.</para>
/// </remarks>
[Trait("Category", "Redshift")]
public sealed class RedshiftFeatureDataAccessIntegrationTests : IAsyncLifetime
{
    private const string TestRedshiftEnvVar = "HONUA_TEST_REDSHIFT";

    private PostgreSqlContainer _container = null!;
    private RedshiftFeatureDataAccess _dataAccess = null!;
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

    [RequiredEnvironmentFact(TestRedshiftEnvVar, "1", skipReason: "Set HONUA_TEST_REDSHIFT=1 to run Redshift (PostgreSQL-wire stand-in) integration tests.")]
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

    [RequiredEnvironmentFact(TestRedshiftEnvVar, "1", skipReason: "Set HONUA_TEST_REDSHIFT=1 to run Redshift (PostgreSQL-wire stand-in) integration tests.")]
    public async Task Select_WithWhereFilter_ReturnsSubset()
    {
        var query = new FeatureQuery { Where = "type = 'commercial'" };

        var count = await _dataAccess.ExecuteCountAsync(
            _mapping, RedshiftFeatureQueryBuilder.BuildCountQuery(_mapping, query), dataConnection: null, CancellationToken.None);

        Assert.True(count is > 0 and < 10);
    }
}
