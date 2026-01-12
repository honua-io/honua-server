// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Postgres.Features.Catalog;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for PostgresLayerCatalog using real PostgreSQL database.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public class PostgresLayerCatalogTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private PostgresLayerCatalog _layerCatalog = null!;
    private string _schemaName = null!;

    public PostgresLayerCatalogTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresLayerCatalogTests));

        // Create layer catalog with the isolated schema
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        _layerCatalog = new PostgresLayerCatalog(connectionProvider, _schemaName);

        // Create catalog table structure
        await _fixture.ExecuteAsync("""
            -- Layers table
            CREATE TABLE layers (
                layer_id integer PRIMARY KEY,
                layer_name varchar(64) NOT NULL,
                description text,
                geometry_type varchar(32) NOT NULL,
                srid integer NOT NULL DEFAULT 4326,
                min_scale double precision,
                max_scale double precision,
                default_visibility boolean NOT NULL DEFAULT true,
                extent geometry,
                metadata jsonb
            );

            -- Layer fields table
            CREATE TABLE layer_fields (
                layer_id integer NOT NULL REFERENCES layers(layer_id),
                field_name varchar(64) NOT NULL,
                field_type varchar(32) NOT NULL,
                field_order integer NOT NULL,
                max_length integer,
                nullable boolean NOT NULL DEFAULT true,
                default_value text,
                description text,
                PRIMARY KEY (layer_id, field_name)
            );

            -- Services table
            CREATE TABLE services (
                service_name varchar(64) PRIMARY KEY,
                description text NOT NULL,
                srid integer NOT NULL DEFAULT 4326,
                max_record_count integer NOT NULL DEFAULT 1000,
                supported_formats text[] NOT NULL DEFAULT '{JSON,GeoJSON}',
                capabilities text[] NOT NULL DEFAULT '{Query,Extract}',
                service_extent geometry,
                metadata jsonb
            );

            -- Service-layer mapping table
            CREATE TABLE service_layers (
                service_name varchar(64) NOT NULL REFERENCES services(service_name),
                layer_id integer NOT NULL REFERENCES layers(layer_id),
                layer_order integer NOT NULL,
                PRIMARY KEY (service_name, layer_id)
            );

            -- Relationships table - defines relationships between layers
            CREATE TABLE relationships (
                layer_id integer NOT NULL REFERENCES layers(layer_id),
                relationship_id integer NOT NULL,
                name varchar(64) NOT NULL,
                related_layer_id integer NOT NULL REFERENCES layers(layer_id),
                relationship_type varchar(64) NOT NULL,
                origin_foreign_key varchar(64) NOT NULL,
                destination_foreign_key varchar(64) NOT NULL,
                description text,
                PRIMARY KEY (layer_id, relationship_id)
            );
            """, _schemaName);

        // Insert test data
        await CreateTestData();

        _output.WriteLine($"Created isolated schema: {_schemaName}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [IntegrationTest]
    public async Task GetLayerAsync_ExistingLayer_ReturnsLayerDefinition()
    {
        // Act
        var layer = await _layerCatalog.GetLayerAsync(1);

        // Assert
        Assert.NotNull(layer);
        Assert.Equal(1, layer.Id);
        Assert.Equal("test_points", layer.Name);
        Assert.Equal("Test point features", layer.Description);
        Assert.Equal(GeometryType.Point, layer.GeometryType);
        Assert.Equal(4326, layer.SpatialReference.Wkid);
        Assert.True(layer.DefaultVisibility);
        Assert.Equal(3, layer.Fields.Length); // id, name, category fields

        // Check extent
        Assert.NotNull(layer.Extent);
        Assert.Equal(-180.0, layer.Extent.Value.MinX);
        Assert.Equal(-90.0, layer.Extent.Value.MinY);
        Assert.Equal(180.0, layer.Extent.Value.MaxX);
        Assert.Equal(90.0, layer.Extent.Value.MaxY);
    }

    [IntegrationTest]
    public async Task GetLayerAsync_NonExistentLayer_ReturnsNull()
    {
        // Act
        var layer = await _layerCatalog.GetLayerAsync(999);

        // Assert
        Assert.Null(layer);
    }

    [IntegrationTest]
    public async Task ListLayersAsync_ReturnsAllLayers()
    {
        // Act
        var layers = await _layerCatalog.ListLayersAsync();

        // Assert
        Assert.Equal(2, layers.Length);

        var pointsLayer = layers.First(l => l.Id == 1);
        Assert.Equal("test_points", pointsLayer.Name);
        Assert.Equal(GeometryType.Point, pointsLayer.GeometryType);
        Assert.Equal(3, pointsLayer.Fields.Length);

        var polygonsLayer = layers.First(l => l.Id == 2);
        Assert.Equal("test_polygons", polygonsLayer.Name);
        Assert.Equal(GeometryType.Polygon, polygonsLayer.GeometryType);
        Assert.Equal(2, polygonsLayer.Fields.Length);
    }

    [IntegrationTest]
    public async Task GetServiceAsync_ExistingService_ReturnsServiceDefinition()
    {
        // Act
        var service = await _layerCatalog.GetServiceAsync("TestService");

        // Assert
        Assert.NotNull(service);
        Assert.Equal("TestService", service.Name);
        Assert.Equal("Test GeoServices service", service.Description);
        Assert.Equal(4326, service.SpatialReference.Wkid);
        Assert.Equal(500, service.MaxRecordCount);
        Assert.Equal(2, service.SupportedFormats.Length);
        Assert.Contains("JSON", service.SupportedFormats);
        Assert.Contains("GeoJSON", service.SupportedFormats);
        Assert.Equal(2, service.Capabilities.Length);
        Assert.Contains("Query", service.Capabilities);
        Assert.Contains("Extract", service.Capabilities);
        Assert.Equal(2, service.Layers.Length);
    }

    [IntegrationTest]
    public async Task GetServiceAsync_NonExistentService_ReturnsNull()
    {
        // Act
        var service = await _layerCatalog.GetServiceAsync("NonExistent");

        // Assert
        Assert.Null(service);
    }

    [IntegrationTest]
    public async Task ListServicesAsync_ReturnsAllServices()
    {
        // Act
        var services = await _layerCatalog.ListServicesAsync();

        // Assert
        Assert.Single(services);

        var service = services[0];
        Assert.Equal("TestService", service.Name);
        Assert.Equal(2, service.Layers.Length);

        // Verify layers are ordered correctly
        Assert.Equal(1, service.Layers[0].Id); // layer_order = 1
        Assert.Equal(2, service.Layers[1].Id); // layer_order = 2
    }

    [IntegrationTest]
    public async Task LayerExistsAsync_ExistingLayer_ReturnsTrue()
    {
        // Act
        var exists = await _layerCatalog.LayerExistsAsync(1);

        // Assert
        Assert.True(exists);
    }

    [IntegrationTest]
    public async Task LayerExistsAsync_NonExistentLayer_ReturnsFalse()
    {
        // Act
        var exists = await _layerCatalog.LayerExistsAsync(999);

        // Assert
        Assert.False(exists);
    }

    [IntegrationTest]
    public async Task ServiceExistsAsync_ExistingService_ReturnsTrue()
    {
        // Act
        var exists = await _layerCatalog.ServiceExistsAsync("TestService");

        // Assert
        Assert.True(exists);
    }

    [IntegrationTest]
    public async Task ServiceExistsAsync_NonExistentService_ReturnsFalse()
    {
        // Act
        var exists = await _layerCatalog.ServiceExistsAsync("NonExistent");

        // Assert
        Assert.False(exists);
    }

    [IntegrationTest]
    public async Task ServiceExistsAsync_CaseInsensitive_ReturnsTrue()
    {
        // Act
        var exists = await _layerCatalog.ServiceExistsAsync("testservice");

        // Assert
        Assert.True(exists);
    }

    [IntegrationTest]
    public async Task GetLayerAsync_LayerFields_AreCorrectlyPopulated()
    {
        // Act
        var layer = await _layerCatalog.GetLayerAsync(1);

        // Assert
        Assert.NotNull(layer);
        Assert.Equal(3, layer.Fields.Length);

        var idField = layer.Fields.First(f => f.Name == "id");
        Assert.Equal(FieldType.BigInteger, idField.Type);
        Assert.False(idField.Nullable);

        var nameField = layer.Fields.First(f => f.Name == "name");
        Assert.Equal(FieldType.String, nameField.Type);
        Assert.Equal(100, nameField.Length);
        Assert.True(nameField.Nullable);

        var categoryField = layer.Fields.First(f => f.Name == "category");
        Assert.Equal(FieldType.String, categoryField.Type);
        Assert.Equal(50, categoryField.Length);
        Assert.Equal("Test category field", categoryField.Description);
    }

    private async Task CreateTestData()
    {
        // Insert test layers
        await _fixture.ExecuteAsync("""
            INSERT INTO layers (layer_id, layer_name, description, geometry_type, srid, default_visibility, extent) VALUES
            (1, 'test_points', 'Test point features', 'Point', 4326, true, ST_MakeEnvelope(-180, -90, 180, 90, 4326)),
            (2, 'test_polygons', 'Test polygon features', 'Polygon', 3857, true, NULL);
            """, _schemaName);

        // Insert layer fields
        await _fixture.ExecuteAsync("""
            INSERT INTO layer_fields (layer_id, field_name, field_type, field_order, max_length, nullable, description) VALUES
            (1, 'id', 'BigInteger', 1, NULL, false, NULL),
            (1, 'name', 'String', 2, 100, true, NULL),
            (1, 'category', 'String', 3, 50, true, 'Test category field'),
            (2, 'id', 'BigInteger', 1, NULL, false, NULL),
            (2, 'area', 'Double', 2, NULL, true, 'Calculated area in square meters');
            """, _schemaName);

        // Insert test service
        await _fixture.ExecuteAsync("""
            INSERT INTO services (service_name, description, srid, max_record_count, supported_formats, capabilities) VALUES
            ('TestService', 'Test GeoServices service', 4326, 500, '{JSON,GeoJSON}', '{Query,Extract}');
            """, _schemaName);

        // Insert service-layer mappings
        await _fixture.ExecuteAsync("""
            INSERT INTO service_layers (service_name, layer_id, layer_order) VALUES
            ('TestService', 1, 1),
            ('TestService', 2, 2);
            """, _schemaName);
    }
}
