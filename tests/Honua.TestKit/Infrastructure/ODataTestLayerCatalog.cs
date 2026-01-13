// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of ILayerCatalog with comprehensive field definitions
/// designed for OData and cross-protocol integration tests.
/// Matches the OData test schema with numeric, boolean, string,
/// and nullable field types.
/// </summary>
public sealed class ODataTestLayerCatalog : ILayerCatalog
{
    private static readonly string[] _supportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _capabilities = ["Query", "Extract"];

    private readonly ServiceDefinition _testService;
    private readonly LayerDefinition _testLayer;
    private readonly LayerDefinition _landmarksLayer;

    public ODataTestLayerCatalog()
    {
        // Create comprehensive field definitions matching OData test data
        var testFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, false, null, "City name"),
            new FieldDefinition("population", FieldType.Integer, null, true, null, "City population"),
            new FieldDefinition("area_sq_km", FieldType.Double, null, true, null, "Area in square kilometers"),
            new FieldDefinition("is_capital", FieldType.Boolean, null, true, null, "Whether city is a state capital"),
            new FieldDefinition("state", FieldType.String, 100, true, null, "State name"),
            new FieldDefinition("country", FieldType.String, 100, true, null, "Country name"),
            new FieldDefinition("founded_year", FieldType.Integer, null, true, null, "Year the city was founded"),
            new FieldDefinition("rating", FieldType.Double, null, true, null, "City rating"),
            new FieldDefinition("notes", FieldType.String, 500, true, null, "Additional notes")
        };

        var spatialRef = SpatialReference.Create(4326); // WGS84
        var extent = FeatureExtent.Create(-125.0, 30.0, -100.0, 50.0, 4326);

        var landmarksFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("city_id", FieldType.Integer, null, false, null, "Origin city ID"),
            new FieldDefinition("name", FieldType.String, 255, false, null, "Landmark name"),
            new FieldDefinition("category", FieldType.String, 100, true, null, "Landmark category"),
            new FieldDefinition("established_year", FieldType.Integer, null, true, null, "Year established")
        };

        var relationship = Relationship.Create(
            relationshipId: 1,
            name: "Landmarks",
            relatedLayerId: 1,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "objectid",
            destinationForeignKeyField: "city_id",
            description: "City to landmark relationship");

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
            DefaultVisibility: true,
            Relationships: [relationship]);

        _landmarksLayer = new LayerDefinition(
            Id: 1,
            Name: "City Landmarks",
            Description: "Landmarks for OData expand tests",
            GeometryType: GeometryType.None,
            SpatialReference: spatialRef,
            Fields: landmarksFields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        _testService = new ServiceDefinition(
            Name: "cities",
            Description: "Cities service for OData integration tests",
            Layers: [_testLayer, _landmarksLayer],
            SpatialReference: spatialRef,
            SupportedFormats: _supportedFormats,
            Capabilities: _capabilities,
            ServiceExtent: extent);
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(layerId switch
        {
            0 => _testLayer,
            1 => _landmarksLayer,
            _ => null
        });
    }

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new[] { _testLayer, _landmarksLayer });
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
        return Task.FromResult(layerId is 0 or 1);
    }

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Equals(serviceName, "cities", StringComparison.OrdinalIgnoreCase));
    }

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        if (layerId != 0)
        {
            return Task.FromResult<Relationship?>(null);
        }

        var relationship = _testLayer.LayerRelationships.FirstOrDefault(r => r.RelationshipId == relationshipId);
        return Task.FromResult<Relationship?>(relationship.RelationshipId == relationshipId ? relationship : null);
    }

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(layerId == 0 ? _testLayer.LayerRelationships : Array.Empty<Relationship>());
    }
}
