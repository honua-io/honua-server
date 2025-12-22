// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Catalog.Abstractions;

/// <summary>
/// Abstraction for layer and service metadata management
/// </summary>
public interface ILayerCatalog
{
    /// <summary>
    /// Retrieves a layer definition by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Layer definition if found, null otherwise</returns>
    Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all available layers in the catalog
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of all layer definitions</returns>
    Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a service definition by name
    /// </summary>
    /// <param name="serviceName">Service name (case-insensitive)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Service definition if found, null otherwise</returns>
    Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all available services in the catalog
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of all service definitions</returns>
    Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a layer exists in the catalog
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if layer exists, false otherwise</returns>
    Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a service exists in the catalog
    /// </summary>
    /// <param name="serviceName">Service name (case-insensitive)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if service exists, false otherwise</returns>
    Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a specific relationship definition for a layer
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="relationshipId">Relationship identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Relationship definition if found, null otherwise</returns>
    Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all relationships defined for a layer
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of relationship definitions for the layer</returns>
    Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default);
}
