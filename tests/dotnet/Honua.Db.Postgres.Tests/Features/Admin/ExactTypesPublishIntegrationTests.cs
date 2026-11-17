// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class ExactTypesPublishIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Amount = "12345678901234.5000";
    private readonly string _serviceName = $"exact_{Guid.NewGuid():N}";
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync("ExactTypes");
        var seed = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "base-schema.sql"));
        await fixture.ApplyGlobalSeedSqlAsync($"SET search_path TO honua, public;\n{seed}");
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        await fixture.ExecuteAsync($"""
            CREATE TABLE "{_schema}".ledger (
                id integer PRIMARY KEY, amount numeric(20,4), signed timestamptz, observed timestamptz,
                geom geometry(Point,4326));
            INSERT INTO "{_schema}".ledger VALUES
                (1, {Amount}, '2024-01-01 14:00:00+02', '2024-01-01 00:00:00+00', ST_SetSRID(ST_MakePoint(1, 2), 4326));
            """);
    }

    public async Task DisposeAsync()
    {
        await fixture.ApplyGlobalSeedSqlAsync("""
            DELETE FROM honua.layer_fields
            WHERE layer_id IN (SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
            DELETE FROM honua.service_layers WHERE service_name = @serviceName;
            DELETE FROM honua.services WHERE service_name = @serviceName;
            """, command => command.Parameters.AddWithValue("serviceName", _serviceName));
        await fixture.DropSchemaAsync(_schema);
    }

    [Fact]
    public async Task PublishWithExactTypes_PersistsExactFieldsAndKeepsThemOnRepublish()
    {
        var service = CreateService();
        var published = await service.PublishLayerAsync(ConnectionString(), ExactRequest());

        var fieldTypes = await ReadFieldTypesAsync(published.StorageLayerId);
        fieldTypes.Should().Contain("amount", "Decimal").And.Contain("signed", "TimestampOffset")
            .And.Contain("observed", "DateTime").And.Contain("id", "Integer");

        var rewritten = await service.RewritePublishedLayerFieldsAsync(
            ConnectionString(), published.StorageLayerId, PlainRequest());

        rewritten.Should().NotBeNull();
        (await ReadFieldTypesAsync(published.StorageLayerId)).Should().BeEquivalentTo(fieldTypes);
    }

    [Fact]
    public async Task ReadPublishedExactRow_ReturnsExactDecimalTextAndOffsetInstant()
    {
        var published = await CreateService().PublishLayerAsync(ConnectionString(), ExactRequest());

        var feature = (await CreateReader().QueryAsync(published.StorageLayerId, new FeatureQuery())).Items.Single();

        Convert.ToString(feature.Attributes["amount"], CultureInfo.InvariantCulture).Should().Be(Amount);
        var signed = Convert.ToString(feature.Attributes["signed"], CultureInfo.InvariantCulture)!;
        ExactAttributeValues.ParseTimestampOffset(signed)
            .Should().Be(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
    }

    private LayerPublishRequest ExactRequest() => new()
    {
        Schema = _schema, Table = "ledger", LayerName = "Ledger", GeometryColumn = "geom", Srid = 4326,
        PrimaryKey = "id", ServiceName = _serviceName, TemporalColumn = "observed", ExactTypes = true
    };

    // A republish that omits the flag, as the GeoServices import replacement path sends it.
    private LayerPublishRequest PlainRequest() => new()
    {
        Schema = _schema, Table = "ledger", LayerName = "Ledger", GeometryColumn = "geom", Srid = 4326,
        PrimaryKey = "id", ServiceName = _serviceName, TemporalColumn = "observed"
    };

    private async Task<Dictionary<string, string>> ReadFieldTypesAsync(int layerId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT field_name, field_type FROM honua.layer_fields WHERE layer_id = @layerId", connection);
        command.Parameters.AddWithValue("layerId", layerId);
        await using var reader = await command.ExecuteReaderAsync();
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            types[reader.GetString(0)] = reader.GetString(1);
        }

        return types;
    }

    private PostgreSqlLayerPublishingService CreateService()
    {
        var schemaConfiguration = new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema, _schema, [_schema, "public"]);
        var graphStore = new PostgresMetadataV2GraphStore(
            new ConnectionProvider(ConnectionString(), fixture.DataSource),
            $"ExactTypes-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        return new PostgreSqlLayerPublishingService(
            new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaContext: null,
                schemaConfiguration: schemaConfiguration),
            graphStore,
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            schemaConfiguration: schemaConfiguration);
    }

    private PostgresStorageMappedFeatureReader CreateReader()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "exact-ledger", Name = "Ledger" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new() { Name = "amount", Type = MetadataV2FieldType.Decimal },
                new() { Name = "signed", Type = MetadataV2FieldType.TimestampOffset },
                new() { Name = "geom", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] }
            ]
        };
        return new PostgresStorageMappedFeatureReader(
            new ConnectionProvider(fixture.ConnectionString, fixture.DataSource),
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource,
            new FeatureStorageMapping("ledger", SchemaName: _schema, PrimaryKeyColumn: "id",
                GeometryColumn: "geom", StorageSrid: 4326),
            connection: null,
            connectionEncryptionService: null);
    }

    // The publishing service references the shared catalog unqualified, so route it to 'honua'.
    private string ConnectionString()
        => new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = "honua,public" }.ConnectionString;

    private sealed class ConnectionProvider(string connectionString, NpgsqlDataSource dataSource)
        : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => connectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
