// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
    private readonly IMetadataV2GraphProvider? _v2Provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceValidator"/> class.
    /// </summary>
    /// <param name="layerCatalog">Layer catalog for metadata lookup</param>
    /// <param name="v2Provider">Optional Metadata v2 graph provider. When provided,
    /// the V2 overloads return results sourced from the canonical graph; when null,
    /// the V2 overloads throw <see cref="NotSupportedException"/>.</param>
    public ResourceValidator(ILayerCatalog layerCatalog, IMetadataV2GraphProvider? v2Provider = null)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _v2Provider = v2Provider;
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

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerId);
        if (pub is null)
        {
            return ResourceValidationResult.NotFound<MetadataV2Resource>(
                ErrorMessages.NotFound.FormatLayer(layerId));
        }
        var resource = snapshot.ResolveResource(pub);
        if (resource is null)
        {
            return ResourceValidationResult.NotFound<MetadataV2Resource>(
                ErrorMessages.NotFound.FormatLayer(layerId));
        }
        return ResourceValidationResult.Success(resource);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return ResourceValidationResult.InvalidIdentifier<MetadataV2Resource>(
                ErrorMessages.Validation.CollectionIdRequired);
        }

        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Match by resource name (case-insensitive) or resource id.
        foreach (var resource in snapshot.Graph.Resources)
        {
            if (!IsRuntimeVisible(resource.Status))
            {
                continue;
            }

            if (string.Equals(resource.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resource.Metadata.Id, collectionId, StringComparison.Ordinal))
            {
                return ResourceValidationResult.Success(resource);
            }
        }

        // Fall back to integer layer index, mirroring v1.
        if (int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerIndex))
        {
            var byIndex = await ValidateLayerV2Async(layerIndex, cancellationToken).ConfigureAwait(false);
            if (byIndex.IsValid)
            {
                return byIndex;
            }
        }

        return ResourceValidationResult.NotFound<MetadataV2Resource>(
            ErrorMessages.NotFound.FormatCollection(collectionId));
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ResourceValidationResult.InvalidIdentifier<MetadataV2Service>(
                ErrorMessages.Validation.ServiceIdRequired);
        }

        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName) &&
            IsRuntimeVisible(byName.Status))
        {
            return ResourceValidationResult.Success(byName);
        }
        if (snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId) &&
            IsRuntimeVisible(byId.Status))
        {
            return ResourceValidationResult.Success(byId);
        }
        return ResourceValidationResult.NotFound<MetadataV2Service>(
            ErrorMessages.NotFound.FormatService(serviceId));
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var serviceResult = await ValidateServiceV2Async(serviceId, cancellationToken).ConfigureAwait(false);
        if (!serviceResult.IsValid)
        {
            var message = serviceResult.ErrorMessage ?? ErrorMessages.NotFound.FormatService(serviceId);
            return serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? ResourceValidationResult.InvalidIdentifier<MetadataV2ServiceLayerTriple>(message)
                : ResourceValidationResult.NotFound<MetadataV2ServiceLayerTriple>(message);
        }
        var service = serviceResult.Resource!;
        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pub in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (pub.LayerIndex != layerId || !IsRuntimeVisible(pub.Status)) continue;
            var resource = snapshot.ResolveResource(pub);
            if (resource is null) continue;
            if (!IsRuntimeVisible(resource.Status)) continue;
            return ResourceValidationResult.Success(new MetadataV2ServiceLayerTriple(service, pub, resource));
        }
        return ResourceValidationResult.NotFound<MetadataV2ServiceLayerTriple>(
            ErrorMessages.NotFound.FormatLayerInService(layerId, serviceId));
    }

    private static bool IsRuntimeVisible(MetadataV2Status? status)
        => status is null ||
           (status.Lifecycle is not (MetadataV2LifecycleStatus.Retired or MetadataV2LifecycleStatus.Archived) &&
            status.State is not MetadataV2OperationalState.Failed);

    private async Task<MetadataV2GraphSnapshot> RequireV2SnapshotAsync(CancellationToken cancellationToken)
    {
        if (_v2Provider is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ResourceValidator)} was constructed without an {nameof(IMetadataV2GraphProvider)}; V2 validation overloads are unavailable.");
        }
        return await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}
