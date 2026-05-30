// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
        CancellationToken cancellationToken);

    Task<ImageServerLayerResolution> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        CancellationToken cancellationToken);
}

internal readonly record struct ImageServerLayerResolution(
    int LayerId,
    IResult? ErrorResult);

internal sealed class MetadataV2ImageServerLayerResolver(
    IResourceValidator resourceValidator,
    IMetadataV2GraphProvider metadataGraphProvider) : IImageServerLayerResolver
{
    public async Task<ImageServerLayerResolution> ResolveFirstAccessibleLayerAsync(
        string serviceId,
        HttpContext context,
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
            return new ImageServerLayerResolution(0, serviceResult.ErrorResult);
        }

        var service = serviceResult.Service!;
        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publishedResources = snapshot.PublicationsForService(service.Metadata.Id)
            .Select(publication => new ImageServerPublicationLayer(
                snapshot.ResolveStorageLayerId(publication),
                snapshot.ResolveResource(publication)))
            .Where(static publication => publication.StorageLayerId.HasValue && publication.Resource is not null)
            .ToArray();

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            publishedResources.Select(static publication => publication.Resource!),
            service);
        if (accessError is not null)
        {
            return new ImageServerLayerResolution(0, accessError);
        }

        var layer = publishedResources.FirstOrDefault(publication =>
            AccessPolicyHelpers.IsResourceAccessible(context, publication.Resource!, service));
        if (layer.Resource is null || !layer.StorageLayerId.HasValue)
        {
            return new ImageServerLayerResolution(
                0,
                StandardErrorHelpers.CreateNotFound(context, "Image service has no layers."));
        }

        return new ImageServerLayerResolution(layer.StorageLayerId.Value, null);
    }

    public async Task<ImageServerLayerResolution> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var candidates = snapshot.Graph.Publications
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
                StandardErrorHelpers.CreateNotFound(context, $"{MetadataV2ServiceProtocols.ImageServer} is not enabled for this service."));
        }

        var accessError = AccessPolicyHelpers.RequireResourceAccess(
            context,
            candidate.Resource,
            candidate.Service);
        if (accessError is not null)
        {
            return new ImageServerLayerResolution(0, accessError);
        }

        return new ImageServerLayerResolution(layerId, null);
    }

    private readonly record struct ImageServerPublicationLayer(
        int? StorageLayerId,
        MetadataV2Resource? Resource);

    private readonly record struct ImageServerPublishedLayer(
        MetadataV2Publication Publication,
        int? StorageLayerId,
        MetadataV2Resource? Resource,
        MetadataV2Service? Service);
}
