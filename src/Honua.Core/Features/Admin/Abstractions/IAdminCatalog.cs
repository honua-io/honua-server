// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>
/// Abstraction for administrative catalog operations (CRUD for services, layers, relationships)
/// </summary>
public interface IAdminCatalog
{
    // Service operations

    /// <summary>
    /// Creates a new service definition
    /// </summary>
    /// <param name="name">Service name</param>
    /// <param name="description">Service description</param>
    /// <param name="spatialReference">Default spatial reference</param>
    /// <param name="metadata">Optional catalog metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created service definition</returns>
    Task<ServiceDefinition> CreateServiceAsync(
        string name,
        string description,
        SpatialReference spatialReference,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing service definition
    /// </summary>
    /// <param name="name">Service name (identifier)</param>
    /// <param name="description">New description</param>
    /// <param name="metadata">Optional catalog metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated service definition, null if not found</returns>
    Task<ServiceDefinition?> UpdateServiceAsync(
        string name,
        string? description = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a service definition
    /// </summary>
    /// <param name="name">Service name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteServiceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds a layer to a service
    /// </summary>
    /// <param name="serviceName">Service name</param>
    /// <param name="layerId">Layer ID to bind</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if binding succeeded</returns>
    Task<bool> BindLayerToServiceAsync(string serviceName, int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unbinds a layer from a service
    /// </summary>
    /// <param name="serviceName">Service name</param>
    /// <param name="layerId">Layer ID to unbind</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if unbinding succeeded</returns>
    Task<bool> UnbindLayerFromServiceAsync(string serviceName, int layerId, CancellationToken cancellationToken = default);

    // Layer operations

    /// <summary>
    /// Creates a new layer definition from a database table
    /// </summary>
    /// <param name="tableName">Database table name</param>
    /// <param name="schemaName">Database schema name</param>
    /// <param name="displayName">Display name for the layer</param>
    /// <param name="description">Layer description</param>
    /// <param name="metadata">Optional catalog metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created layer definition</returns>
    Task<LayerDefinition> CreateLayerAsync(
        string tableName,
        string schemaName,
        string displayName,
        string? description = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing layer definition
    /// </summary>
    /// <param name="layerId">Layer ID</param>
    /// <param name="displayName">New display name</param>
    /// <param name="description">New description</param>
    /// <param name="minScale">Minimum visibility scale</param>
    /// <param name="maxScale">Maximum visibility scale</param>
    /// <param name="defaultVisibility">Default visibility</param>
    /// <param name="metadata">Optional catalog metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated layer definition, null if not found</returns>
    Task<LayerDefinition?> UpdateLayerAsync(
        int layerId,
        string? displayName = null,
        string? description = null,
        double? minScale = null,
        double? maxScale = null,
        bool? defaultVisibility = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a layer definition
    /// </summary>
    /// <param name="layerId">Layer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes layer metadata from the underlying database table
    /// </summary>
    /// <param name="layerId">Layer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Refreshed layer definition, null if not found</returns>
    Task<LayerDefinition?> RefreshLayerAsync(int layerId, CancellationToken cancellationToken = default);

    // Relationship operations

    /// <summary>
    /// Creates a relationship between layers
    /// </summary>
    /// <param name="originLayerId">Origin layer ID</param>
    /// <param name="relatedLayerId">Related layer ID</param>
    /// <param name="name">Relationship name</param>
    /// <param name="relationshipType">Type (e.g., "OneToMany", "ManyToMany")</param>
    /// <param name="originForeignKey">Foreign key field in origin layer</param>
    /// <param name="destinationForeignKey">Foreign key field in destination layer</param>
    /// <param name="description">Relationship description</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created relationship</returns>
    Task<Relationship> CreateRelationshipAsync(
        int originLayerId,
        int relatedLayerId,
        string name,
        string relationshipType,
        string originForeignKey,
        string destinationForeignKey,
        string? description = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a relationship
    /// </summary>
    /// <param name="layerId">Layer ID</param>
    /// <param name="relationshipId">Relationship ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default);
}
