// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Test implementation of ILayerCatalog that extends TestLayerCatalog
/// to provide relationship support for queryRelatedRecords endpoint tests.
/// </summary>
public sealed class TestLayerCatalogWithRelationships : ILayerCatalog
{
    private readonly TestLayerCatalog _baseCatalog = new();
    private readonly Dictionary<(int layerId, int relationshipId), Relationship> _relationships = new();

    public TestLayerCatalogWithRelationships()
    {
        // Set up test relationships for the test layers
        var testRelationship = Relationship.Create(
            relationshipId: 1,
            name: "Test Relationship",
            relatedLayerId: 1,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "related_id",
            destinationForeignKeyField: "objectid",
            description: "Test relationship between layer 0 and layer 1");

        _relationships[(0, 1)] = testRelationship;

        // Add another relationship for testing
        var secondTestRelationship = Relationship.Create(
            relationshipId: 2,
            name: "Secondary Relationship",
            relatedLayerId: 2,
            relationshipType: "esriRelRoleOrigin",
            originForeignKeyField: "secondary_id",
            destinationForeignKeyField: "objectid",
            description: "Secondary test relationship");

        _relationships[(0, 2)] = secondTestRelationship;
    }

    // Delegate all existing catalog methods to the base implementation
    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => _baseCatalog.GetLayerAsync(layerId, cancellationToken);

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => _baseCatalog.ListLayersAsync(cancellationToken);

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => _baseCatalog.GetServiceAsync(serviceName, cancellationToken);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => _baseCatalog.ListServicesAsync(cancellationToken);

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => _baseCatalog.LayerExistsAsync(layerId, cancellationToken);

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
