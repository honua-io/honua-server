// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of ILayerCatalog that extends TestLayerCatalog
/// to provide relationship support for queryRelatedRecords endpoint tests.
/// </summary>
public sealed class TestLayerCatalogWithRelationships : ILayerCatalog
{
    private readonly TestLayerCatalog _baseCatalog = new();
    private readonly Dictionary<(int layerId, int relationshipId), Relationship> _relationships = new();
    private readonly Dictionary<int, LayerDefinition> _relatedLayers = new();

    public TestLayerCatalogWithRelationships()
    {
        // Create related layer definitions for relationship testing
        var relatedFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name field"),
            new FieldDefinition("related_id", FieldType.Integer, null, true, null, "Foreign key to origin layer")
        };

        var secondaryFields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name field"),
            new FieldDefinition("secondary_id", FieldType.Integer, null, true, null, "Foreign key to origin layer"),
            new FieldDefinition("type", FieldType.String, 100, true, null, "Type field")
        };

        var spatialRef = new SpatialReference(4326);
        var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);

        // Create related layer 1
        _relatedLayers[1] = new LayerDefinition(
            Id: 1,
            Name: "Related Test Layer 1",
            Description: "Related layer for relationship testing",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialRef,
            Fields: relatedFields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        // Create related layer 2
        _relatedLayers[2] = new LayerDefinition(
            Id: 2,
            Name: "Secondary Related Layer",
            Description: "Secondary related layer for relationship testing",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialRef,
            Fields: secondaryFields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        // Set up test relationships for the test layers
        var testRelationship = Relationship.Create(
            relationshipId: 1,
            name: "Test Relationship",
            relatedLayerId: 1,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "objectid",
            destinationForeignKeyField: "related_id",
            description: "Test relationship between layer 0 and layer 1");

        _relationships[(0, 1)] = testRelationship;

        // Add another relationship for testing
        var secondTestRelationship = Relationship.Create(
            relationshipId: 2,
            name: "Secondary Relationship",
            relatedLayerId: 2,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "objectid",
            destinationForeignKeyField: "secondary_id",
            description: "Secondary test relationship");

        _relationships[(0, 2)] = secondTestRelationship;
    }

    // Override layer methods to provide related layers in addition to base catalog layers
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Check if it's a related layer first
        if (_relatedLayers.TryGetValue(layerId, out var relatedLayer))
        {
            return relatedLayer;
        }

        // Fall back to base catalog for layer 0
        return await _baseCatalog.GetLayerAsync(layerId, cancellationToken);
    }

    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        var baseLayers = await _baseCatalog.ListLayersAsync(cancellationToken);
        var allLayers = new List<LayerDefinition>(baseLayers);
        allLayers.AddRange(_relatedLayers.Values);
        return allLayers.ToArray();
    }

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => _baseCatalog.GetServiceAsync(serviceName, cancellationToken);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => _baseCatalog.ListServicesAsync(cancellationToken);

    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Check if it's a related layer first
        if (_relatedLayers.ContainsKey(layerId))
        {
            return true;
        }

        // Fall back to base catalog for layer 0
        return await _baseCatalog.LayerExistsAsync(layerId, cancellationToken);
    }

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => _baseCatalog.ServiceExistsAsync(serviceName, cancellationToken);

    // Implement relationship-specific methods
    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        _relationships.TryGetValue((layerId, relationshipId), out var relationship);
        return Task.FromResult<Relationship?>(relationship);
    }

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var layerRelationships = _relationships
            .Where(kvp => kvp.Key.layerId == layerId)
            .Select(kvp => kvp.Value)
            .ToArray();

        return Task.FromResult(layerRelationships);
    }
}
