// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal interface IImageServerLayerResolver
{
    Task<ImageServerLayerResolution> ResolveFirstAccessibleLayerAsync(
        string serviceId,
        HttpContext context,
        AuthorizationOperation operation,
        CancellationToken cancellationToken);

    Task<ImageServerLayerResolution> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        AuthorizationOperation operation,
        CancellationToken cancellationToken);
}

internal readonly record struct ImageServerLayerResolution(
    int LayerId,
    string? PublicationId,
    int? PublicationLayerIndex,
    IResult? ErrorResult);

internal sealed class MetadataV2ImageServerLayerResolver(
    IResourceValidator resourceValidator,
    IMetadataV2GraphProvider metadataGraphProvider,
    IRasterStore rasterStore) : IImageServerLayerResolver
{
    public async Task<ImageServerLayerResolution> ResolveFirstAccessibleLayerAsync(
        string serviceId,
        HttpContext context,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            MetadataV2ServiceProtocols.ImageServer,
            context,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!serviceResult.IsValid)
        {
            return new ImageServerLayerResolution(0, null, null, serviceResult.ErrorResult);
        }

        var service = serviceResult.Service!;
        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publishedResources = snapshot.PublicationsForService(service.Metadata.Id)
            .Where(snapshot.IsRoutable)
            .Select(publication => new ImageServerPublicationLayer(
                publication.Metadata.Id,
                publication.LayerIndex,
                snapshot.ResolveStorageLayerId(publication),
                snapshot.ResolveResource(publication)))
            .Where(static publication =>
                publication.StorageLayerId.HasValue
                && publication.Resource is not null)
            .ToArray();

        var layer = default(ImageServerPublicationLayer);
        IResult? firstAccessError = null;
        var hasAuthorizedResource = false;
        foreach (var publication in publishedResources)
        {
            var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
                context,
                publication.Resource!,
                operation,
                service,
                cancellationToken).ConfigureAwait(false);
            if (accessError is not null)
            {
                firstAccessError ??= accessError;
                continue;
            }

            hasAuthorizedResource = true;
            var raster = await rasterStore.GetPrimaryRasterInfoAsync(
                publication.StorageLayerId!.Value,
                cancellationToken).ConfigureAwait(false);
            if (raster is not null)
            {
                layer = publication;
                break;
            }
        }

        if (layer.Resource is null || !layer.StorageLayerId.HasValue)
        {
            if (!hasAuthorizedResource && firstAccessError is not null)
            {
                return new ImageServerLayerResolution(0, null, null, firstAccessError);
            }

            return new ImageServerLayerResolution(
                0,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, "Image service has no raster layers."));
        }

        return new ImageServerLayerResolution(
            layer.StorageLayerId.Value,
            layer.PublicationId,
            layer.PublicationLayerIndex,
            null);
    }

    public async Task<ImageServerLayerResolution> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var candidates = snapshot.Graph.Publications
            .Where(snapshot.IsRoutable)
            .Select(publication => new ImageServerPublishedLayer(
                publication,
                snapshot.ResolveStorageLayerId(publication),
                snapshot.ResolveResource(publication),
                snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ? service : null))
            .Where(candidate => candidate.StorageLayerId == layerId && candidate.Resource is not null)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new ImageServerLayerResolution(
                0,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Layer {layerId} not found"));
        }

        var candidate = candidates
            .Where(static candidate =>
                candidate.Service is not null &&
                MetadataV2ServiceProtocols.IsProtocolEnabled(candidate.Service, MetadataV2ServiceProtocols.ImageServer))
            .OrderByDescending(static candidate => candidate.Publication.IsPrimary)
            .ThenBy(static candidate => candidate.Service!.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (candidate.Resource is null)
        {
            return new ImageServerLayerResolution(
                0,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"{MetadataV2ServiceProtocols.ImageServer} is not enabled for this service."));
        }

        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context,
            candidate.Resource,
            operation,
            candidate.Service,
            cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return new ImageServerLayerResolution(0, null, null, accessError);
        }

        return new ImageServerLayerResolution(
            snapshot.ResolveStorageLayerId(candidate.Publication) ?? layerId,
            candidate.Publication.Metadata.Id,
            candidate.Publication.LayerIndex,
            null);
    }

    private readonly record struct ImageServerPublicationLayer(
        string PublicationId,
        int? PublicationLayerIndex,
        int? StorageLayerId,
        MetadataV2Resource? Resource);

    private readonly record struct ImageServerPublishedLayer(
        MetadataV2Publication Publication,
        int? StorageLayerId,
        MetadataV2Resource? Resource,
        MetadataV2Service? Service);
}
