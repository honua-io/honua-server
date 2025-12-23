// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of ILayerCatalog with comprehensive field definitions
/// designed for OData and cross-protocol integration tests.
/// Matches the ODataTestFeatureStore schema with numeric, boolean, string,
/// and nullable field types.
/// </summary>
public sealed class ODataTestLayerCatalog : ILayerCatalog
{
    private static readonly string[] SupportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] Capabilities = ["Query", "Extract"];

    private readonly ServiceDefinition _testService;
    private readonly LayerDefinition _testLayer;

    public ODataTestLayerCatalog()
    {
        // Create comprehensive field definitions matching ODataTestFeatureStore
        var testFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, false, null, "City name"),
            new FieldDefinition("population", FieldType.Integer, null, true, null, "City population"),
            new FieldDefinition("area_sq_km", FieldType.Double, null, true, null, "Area in square kilometers"),
            new FieldDefinition("is_capital", FieldType.SmallInteger, null, true, null, "Whether city is a state capital (0/1)"),
            new FieldDefinition("state", FieldType.String, 100, true, null, "State name"),
            new FieldDefinition("country", FieldType.String, 100, true, null, "Country name"),
            new FieldDefinition("founded_year", FieldType.Integer, null, true, null, "Year the city was founded"),
            new FieldDefinition("rating", FieldType.Double, null, true, null, "City rating"),
            new FieldDefinition("notes", FieldType.String, 500, true, null, "Additional notes")
        };

        var spatialRef = new SpatialReference(4326); // WGS84
        var extent = FeatureExtent.Create(-125.0, 30.0, -100.0, 50.0, 4326);

        _testLayer = new LayerDefinition(
            Id: 0,
            Name: "US Cities",
            Description: "US Cities layer for OData integration tests",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialRef,
            Fields: testFields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        _testService = new ServiceDefinition(
            Name: "cities",
            Description: "Cities service for OData integration tests",
            Layers: [_testLayer],
            SpatialReference: spatialRef,
            MaxRecordCount: 1000,
            SupportedFormats: SupportedFormats,
            Capabilities: Capabilities,
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
        return Task.FromResult(string.Equals(serviceName, "cities", StringComparison.OrdinalIgnoreCase)
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
        return Task.FromResult(string.Equals(serviceName, "cities", StringComparison.OrdinalIgnoreCase));
    }

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Relationship?>(null);
    }

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Array.Empty<Relationship>());
    }
}
