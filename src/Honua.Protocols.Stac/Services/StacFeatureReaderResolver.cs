// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Resolves the feature reader and storage-layer handle used by STAC data endpoints.
/// </summary>
internal static class StacFeatureReaderResolver
{
    internal readonly record struct Resolution(IFeatureReader Reader, int StorageLayerId);

    public static async Task<Resolution> ResolveAsync(
        HttpContext context,
        IFeatureReader fallbackReader,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int publicLayerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(publication.StorageBindingId))
        {
            return new Resolution(fallbackReader, publicLayerId);
        }

        var providerQueryRouter = context.RequestServices.GetService<FeatureProviderQueryRouter>()
            ?? throw new InvalidOperationException(
                $"Provider routing is unavailable for storage-bound STAC publication '{publication.Metadata.Id}'.");
        if (service is null)
        {
            throw new InvalidOperationException(
                $"Service routing is unavailable for storage-bound STAC publication '{publication.Metadata.Id}'.");
        }

        if (!snapshot.Index.StorageBindingsById.TryGetValue(publication.StorageBindingId, out var storageBinding))
        {
            throw new InvalidOperationException(
                $"Storage-bound STAC publication '{publication.Metadata.Id}' does not resolve its declared binding '{publication.StorageBindingId}'.");
        }

        var storageLayerId = storageBinding.StorageLayerId
            ?? throw new InvalidOperationException(
                $"Storage binding '{storageBinding.Metadata.Id}' for STAC publication '{publication.Metadata.Id}' does not define a storage layer id.");
        var reader = await providerQueryRouter.ResolveReaderAsync(
                snapshot,
                service,
                resource,
                publication,
                storageLayerId,
                FeatureProviderReadOperation.Query,
                cancellationToken)
            .ConfigureAwait(false);

        return new Resolution(reader, storageLayerId);
    }
}
