// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Oracle.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;
using Xunit;

namespace Honua.Db.Oracle.Tests;

[Trait("Category", "Oracle")]
public sealed class OracleRealDatabaseIntegrationTests : IAsyncLifetime
{
    private const string TestOracleEnvVar = "HONUA_TEST_ORACLE";
    private const int LayerId = 1;
    private const string TableName = "HONUA_SMOKE_PARCELS";

    private OracleContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!ShouldRun)
        {
            return;
        }

        _container = new OracleBuilder()
            // NOT the "slim" variant: gvenzl/oracle-free's slim images strip out the
            // Oracle Spatial component entirely (SDO_GEOMETRY is not a recognized datatype
            // there — confirmed empirically while building this lane, "ORA-00902: invalid
            // datatype" on CREATE TABLE), and this lane's whole point is proving the
            // provider's SDO_GEOMETRY/WKB decode path against a real instance.
            .WithImage("gvenzl/oracle-free:23-faststart")
            .Build();
        await _container.StartAsync().ConfigureAwait(false);
        _connectionString = _container.GetConnectionString();

        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, $"""
            CREATE TABLE {TableName} (
                objectid NUMBER(19) NOT NULL PRIMARY KEY,
                shape SDO_GEOMETRY,
                name VARCHAR2(64),
                area NUMBER
            )
            """).ConfigureAwait(false);

        for (var i = 1; i <= 5; i++)
        {
            var lon = -122.0 + (i * 0.01);
            var lat = 37.0 + (i * 0.01);
            await ExecuteAsync(connection, FormattableString.Invariant($"""
                INSERT INTO {TableName} (objectid, shape, name, area)
                VALUES ({i}, SDO_GEOMETRY(2001, 4326, SDO_POINT_TYPE({lon}, {lat}, NULL), NULL, NULL), 'Parcel {i}', {i * 100.5})
                """)).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, "COMMIT").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool ShouldRun
        => string.Equals(Environment.GetEnvironmentVariable(TestOracleEnvVar), "1", StringComparison.Ordinal);

    [RequiredOracleEnvironmentFact]
    public async Task Connection_OpensAgainstRealOracleFree()
    {
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [RequiredOracleEnvironmentFact]
    public async Task QueryBuildAndExecute_AgainstRealTable_ReturnsSeededRows()
    {
        var mapping = BuildMapping();
        var dataAccess = CreateDataAccess();

        var query = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, new FeatureQuery(), ["NAME", "AREA"]);
        var features = await dataAccess.ExecuteSelectAsync(mapping, query, ["NAME", "AREA"], dataConnection: null, CancellationToken.None);

        Assert.Equal(5, features.Length);
        Assert.All(features, f => Assert.NotNull(f.Geometry));
    }

    [RequiredOracleEnvironmentFact]
    public async Task QueryBuildAndExecute_WithWhereClause_ReturnsFilteredRow()
    {
        var mapping = BuildMapping();
        var dataAccess = CreateDataAccess();

        var query = OracleFeatureQueryBuilder.BuildSelectQuery(
            mapping,
            new FeatureQuery { Where = "name = 'Parcel 3'" },
            ["NAME", "AREA"]);
        var features = await dataAccess.ExecuteSelectAsync(mapping, query, ["NAME", "AREA"], dataConnection: null, CancellationToken.None);

        var feature = Assert.Single(features);
        Assert.Equal(3, feature.Id);
    }

    [RequiredOracleEnvironmentFact]
    public async Task CountAsync_AgainstRealTable_ReturnsRowCount()
    {
        var mapping = BuildMapping();
        var dataAccess = CreateDataAccess();

        var countQuery = OracleFeatureQueryBuilder.BuildCountQuery(mapping, new FeatureQuery());
        var count = await dataAccess.ExecuteCountAsync(mapping, countQuery, dataConnection: null, CancellationToken.None);

        Assert.Equal(5, count);
    }

    [RequiredOracleEnvironmentFact]
    public async Task WkbDecode_AgainstRealSdoGeometry_RoundTripsCoordinates()
    {
        var mapping = BuildMapping();
        var dataAccess = CreateDataAccess();

        var query = OracleFeatureQueryBuilder.BuildSelectQuery(
            mapping,
            new FeatureQuery { Where = "name = 'Parcel 1'" },
            ["NAME"]);
        var features = await dataAccess.ExecuteSelectAsync(mapping, query, ["NAME"], dataConnection: null, CancellationToken.None);

        var feature = Assert.Single(features);
        Assert.NotNull(feature.Geometry);

        var reader = new WKBReader();
        var geometry = reader.Read(feature.Geometry);

        Assert.IsType<Point>(geometry);
        var point = (Point)geometry;
        Assert.Equal(-121.99, point.X, precision: 2);
        Assert.Equal(37.01, point.Y, precision: 2);
    }

    private static OracleLayerMapping BuildMapping()
    {
        var storage = new LayerStorageMapping(
            TableName: TableName,
            SchemaName: null,
            CatalogName: null,
            DatabaseName: null,
            PrimaryKeyColumn: "OBJECTID",
            GeometryColumn: "SHAPE",
            StorageSrid: 4326);

        return OracleLayerMapping.FromStorage(LayerId, storage);
    }

    private OracleFeatureDataAccess CreateDataAccess()
    {
        var connectionFactory = new RealOracleConnectionFactory(_connectionString!);
        return new OracleFeatureDataAccess(
            connectionFactory,
            Options.Create(new OracleOptions()),
            NullLogger<OracleFeatureDataAccess>.Instance);
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class RealOracleConnectionFactory(string connectionString) : IOracleConnectionFactory
    {
        public async Task<OracleConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
        {
            var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiredOracleEnvironmentFactAttribute : FactAttribute
{
    public RequiredOracleEnvironmentFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HONUA_TEST_ORACLE"), "1", StringComparison.Ordinal))
        {
            Skip = "Set HONUA_TEST_ORACLE=1 (Docker required) to run the real-Oracle integration lane.";
        }
    }
}
