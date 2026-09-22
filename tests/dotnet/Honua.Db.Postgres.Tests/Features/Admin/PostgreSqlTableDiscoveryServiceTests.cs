// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Admin;

/// <summary>
/// Regression tests for <see cref="PostgreSqlTableDiscoveryService"/> focusing
/// on interoperability between the public <c>DbConnection</c> overload and the
/// gated provider wrapper (<c>SemaphoreReleasingConnection</c>).
/// </summary>
[Collection("Database")]
public sealed class PostgreSqlTableDiscoveryServiceTests
{
    private readonly PostgresFixture _fixture;

    public PostgreSqlTableDiscoveryServiceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DiscoverNonSpatialTableAsync_PreservesExclusionsAndUnwrapsProviderConnection()
    {
        var schema = await _fixture.CreateIsolatedSchemaAsync("table_discovery");
        try
        {
            var inner = await _fixture.DataSource.OpenConnectionAsync();
            await using var connection = new SemaphoreReleasingConnection(inner, static () => { });
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TABLE "{schema}".attributes (id integer PRIMARY KEY, name text);
                    CREATE TABLE "{schema}".__honua_wfs_stage_attributes (id integer PRIMARY KEY);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var service = new PostgreSqlTableDiscoveryService(NullLogger<PostgreSqlTableDiscoveryService>.Instance);
            var table = await service.DiscoverNonSpatialTableAsync(connection, schema, "attributes");
            table.Should().NotBeNull();
            table!.GeometryColumn.Should().BeNull();
            table.Columns.Should().Contain(column => column.Name == "id" && column.IsPrimaryKey);
            (await service.DiscoverNonSpatialTableAsync(connection, schema, "__honua_wfs_stage_attributes")).Should().BeNull();
            (await service.DiscoverNonSpatialTableAsync(connection, "pg_catalog", "pg_class")).Should().BeNull();
            (await service.DiscoverNonSpatialTableAsync(connection, "information_schema", "sql_features")).Should().BeNull();
            (await service.DiscoverNonSpatialTableAsync(connection, "honua", "layers")).Should().BeNull();

            var configured = new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaConfiguration: new PostgresSchemaConfiguration(schema, "public", ["public"]));
            (await configured.DiscoverNonSpatialTableAsync(connection, schema, "attributes")).Should().BeNull();
        }
        finally
        {
            await _fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task DiscoverPostGisTablesAsync_AcceptsSemaphoreReleasingConnectionWrapper()
    {
        // Regression — passing a provider-opened connection (wrapped in
        // SemaphoreReleasingConnection) through the public DbConnection
        // overload must not throw; the wrapper must be unwrapped transparently.
        var service = new PostgreSqlTableDiscoveryService(
            NullLogger<PostgreSqlTableDiscoveryService>.Instance);

        var inner = await _fixture.DataSource.OpenConnectionAsync();
        await using var wrapped = new SemaphoreReleasingConnection(inner, static () => { });

        var tables = await service.DiscoverPostGisTablesAsync(wrapped, CancellationToken.None);

        tables.Should().NotBeNull();
    }

    [Fact]
    public async Task DiscoverPostGisTablesAsync_ExcludesWfsOverwriteStagingTables()
    {
        var schemaName = await _fixture.CreateIsolatedSchemaAsync("wfs_stage_discovery");
        try
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE TABLE \"{schemaName}\".\"__honua_wfs_stage_cities\" (geom geometry(Point, 4326))";
                await command.ExecuteNonQueryAsync();
            }

            var service = new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaConfiguration: new PostgresSchemaConfiguration(
                    PostgresSchemaConfiguration.DefaultMetadataSchema,
                    schemaName,
                    [schemaName]));

            var tables = await service.DiscoverPostGisTablesAsync(connection, CancellationToken.None);

            tables.Should().NotContain(table => table.Schema == schemaName && table.Table == "__honua_wfs_stage_cities");
        }
        finally
        {
            await _fixture.DropSchemaAsync(schemaName);
        }
    }
}
