// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Unified resource validation service for consistent service/layer/collection existence checks
/// across all protocols (GeoServices REST, OGC API Features, OData).
/// </summary>
/// <remarks>
/// <para>
/// This implementation consolidates resource validation patterns that were previously scattered
/// across protocol-specific implementations. All protocols should use this service for
/// resource existence validation to ensure consistent behavior.
/// </para>
/// </remarks>
internal sealed class ResourceValidator : IResourceValidator
{
    private readonly ILayerCatalog _layerCatalog;

    public ResourceValidator(ILayerCatalog layerCatalog)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<LayerDefinition>> ValidateLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);

        if (layer == null)
        {
            return ResourceValidationResult.NotFound<LayerDefinition>($"Layer {layerId} not found.");
        }

        return ResourceValidationResult.Success(layer);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<LayerDefinition>> ValidateCollectionAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return ResourceValidationResult.InvalidIdentifier<LayerDefinition>("Collection ID is required.");
        }

        if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return ResourceValidationResult.NotFound<LayerDefinition>($"Collection '{collectionId}' not found.");
        }

        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);

        if (layer == null)
        {
            return ResourceValidationResult.NotFound<LayerDefinition>($"Collection '{collectionId}' not found.");
        }

        return ResourceValidationResult.Success(layer);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<ServiceDefinition>> ValidateServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ResourceValidationResult.InvalidIdentifier<ServiceDefinition>("Service ID is required.");
        }

        var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);

        if (service == null)
        {
            return ResourceValidationResult.NotFound<ServiceDefinition>($"Service '{serviceId}' not found.");
        }

        return ResourceValidationResult.Success(service);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<(ServiceDefinition Service, LayerDefinition Layer)>> ValidateServiceLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        // First validate the service exists
        var serviceResult = await ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(
                serviceResult.ErrorMessage ?? $"Service '{serviceId}' not found.");
        }

        var service = serviceResult.Resource!;

        // Find the layer within the service (more efficient than separate layer lookup)
        var layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null)
        {
            return ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(
                $"Layer {layerId} not found in service '{serviceId}'.");
        }

        return ResourceValidationResult.Success((service, layer));
    }
}
