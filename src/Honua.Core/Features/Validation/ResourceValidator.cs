// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
    private readonly IMetadataV2GraphProvider _v2Provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceValidator"/> class.
    /// </summary>
    /// <param name="v2Provider">Metadata v2 graph provider for canonical resource lookup.</param>
    public ResourceValidator(IMetadataV2GraphProvider v2Provider)
    {
        _v2Provider = v2Provider ?? throw new ArgumentNullException(nameof(v2Provider));
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
        if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName))
        {
            return ResourceValidationResult.Success(byName);
        }
        if (snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId))
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
            if (pub.LayerIndex != layerId) continue;
            var resource = snapshot.ResolveResource(pub);
            if (resource is null) continue;
            return ResourceValidationResult.Success(new MetadataV2ServiceLayerTriple(service, pub, resource));
        }
        return ResourceValidationResult.NotFound<MetadataV2ServiceLayerTriple>(
            ErrorMessages.NotFound.FormatLayerInService(layerId, serviceId));
    }

    private async Task<MetadataV2GraphSnapshot> RequireV2SnapshotAsync(CancellationToken cancellationToken)
    {
        return await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}
