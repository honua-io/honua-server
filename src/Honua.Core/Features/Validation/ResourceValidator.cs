// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Core.Features.Validation;

/// <summary>
/// Unified resource validation service for consistent service/layer/collection existence checks
/// across all protocols (GeoServices REST, OGC API Features, OData).
/// </summary>
public sealed class ResourceValidator : IResourceValidator
{
    private readonly ILayerCatalog _layerCatalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceValidator"/> class.
    /// </summary>
    /// <param name="layerCatalog">Layer catalog for metadata lookup</param>
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
            return ResourceValidationResult.NotFound<LayerDefinition>(ErrorMessages.NotFound.FormatLayer(layerId));
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
            return ResourceValidationResult.InvalidIdentifier<LayerDefinition>(ErrorMessages.Validation.CollectionIdRequired);
        }

        var layer = await ResolveLayerByCollectionIdAsync(collectionId, cancellationToken);
        if (layer == null &&
            int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
        }

        if (layer == null)
        {
            return ResourceValidationResult.NotFound<LayerDefinition>(ErrorMessages.NotFound.FormatCollection(collectionId));
        }

        return ResourceValidationResult.Success(layer);
    }

    private async Task<LayerDefinition?> ResolveLayerByCollectionIdAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        return layers
            .OrderBy(layer => layer.Id)
            .FirstOrDefault(layer => string.Equals(layer.Name, collectionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<ServiceDefinition>> ValidateServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ResourceValidationResult.InvalidIdentifier<ServiceDefinition>(ErrorMessages.Validation.ServiceIdRequired);
        }

        var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);

        if (service == null)
        {
            return ResourceValidationResult.NotFound<ServiceDefinition>(ErrorMessages.NotFound.FormatService(serviceId));
        }

        return ResourceValidationResult.Success(service);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<(ServiceDefinition Service, LayerDefinition Layer)>> ValidateServiceLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var serviceResult = await ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            var message = serviceResult.ErrorMessage ?? ErrorMessages.NotFound.FormatService(serviceId);
            return serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? ResourceValidationResult.InvalidIdentifier<(ServiceDefinition, LayerDefinition)>(message)
                : ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(message);
        }

        var service = serviceResult.Resource!;
        var layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null)
        {
            return ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(
                ErrorMessages.NotFound.FormatLayerInService(layerId, serviceId));
        }

        return ResourceValidationResult.Success((service, layer));
    }
}
