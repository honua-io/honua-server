// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of ILayerCatalog that provides static test data
/// for FeatureServer endpoint tests. This is a minimal implementation
/// to make tests pass while the actual PostgreSQL implementation is being fixed.
/// </summary>
public sealed class TestLayerCatalog : ILayerCatalog
{
    private static readonly string[] _supportedFormats = new[] { "JSON", "GeoJSON" };
    private static readonly string[] _capabilities = new[] { "Query", "Extract" };

    private readonly ServiceDefinition _testService;
    private readonly LayerDefinition _testLayer;

    public TestLayerCatalog()
    {
        // Create a test layer with minimal required fields
        var testFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name field"),
            new FieldDefinition("description", FieldType.String, 500, true, null, "Description"),
            new FieldDefinition("category", FieldType.String, 255, true, null, "Category"),
            new FieldDefinition("timestamp", FieldType.DateTime, null, true, null, "Timestamp")
        };

        var spatialRef = new SpatialReference(4326); // WGS84
        var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);

        _testLayer = new LayerDefinition(
            Id: 0,
            Name: "Test Layer",
            Description: "Test layer for integration tests",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialRef,
            Fields: testFields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        // Create test service containing the test layer
        _testService = new ServiceDefinition(
            Name: "test",
            Description: "Test service for integration tests",
            Layers: new[] { _testLayer },
            SpatialReference: spatialRef,
            MaxRecordCount: 1000,
            SupportedFormats: _supportedFormats,
            Capabilities: _capabilities,
            ServiceExtent: extent);
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(layerId == 0 ? _testLayer : null);
    }

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new[] { _testLayer });
    }

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Equals(serviceName, "test", StringComparison.OrdinalIgnoreCase)
            ? _testService : null);
    }

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new[] { _testService });
    }

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(layerId == 0);
    }

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Equals(serviceName, "test", StringComparison.OrdinalIgnoreCase));
    }

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        // Basic implementation returns null - tests that need relationships should use TestLayerCatalogWithRelationships
        return Task.FromResult<Relationship?>(null);
    }

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Basic implementation returns empty array - tests that need relationships should use TestLayerCatalogWithRelationships
        return Task.FromResult(Array.Empty<Relationship>());
    }
}
