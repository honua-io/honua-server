// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Tiles.Services;

public sealed class TileFeatureProviderResolver(FeatureProviderQueryRouter? providerQueryRouter)
{
    private readonly FeatureProviderQueryRouter? _providerQueryRouter = providerQueryRouter;

    public async Task<IFeatureReader> ResolveFeatureReaderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        IFeatureReader fallbackReader,
        CancellationToken cancellationToken)
        => (await ResolveFeatureReaderWithStorageAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            fallbackReader,
            fallbackStorageSrid: null,
            cancellationToken).ConfigureAwait(false)).Reader;

    public async Task<TileFeatureReaderResolution> ResolveFeatureReaderWithStorageAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        IFeatureReader fallbackReader,
        int? fallbackStorageSrid,
        CancellationToken cancellationToken)
    {
        if (!RequiresRouting(snapshot, publication))
        {
            return new TileFeatureReaderResolution(fallbackReader, fallbackStorageSrid);
        }

        var router = RequireRouter();
        var binding = await router.ResolveBindingAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        var reader = binding.Provider is IBindableFeatureDataProvider bindableProvider
            ? bindableProvider.CreateReaderForBinding(binding)
            : binding.Provider.Reader;

        return new TileFeatureReaderResolution(
            reader,
            binding.StorageMapping.StorageSrid ?? fallbackStorageSrid);
    }

    public async Task<TileProviderResolution> ResolveTileProviderAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        ITileProvider fallbackProvider,
        CancellationToken cancellationToken)
    {
        if (!RequiresRouting(snapshot, publication))
        {
            return new TileProviderResolution(fallbackProvider, null);
        }

        var router = RequireRouter();
        var binding = await router.ResolveBindingAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        if (binding.Provider is IBindableTileProvider bindableTileProvider)
        {
            return new TileProviderResolution(
                bindableTileProvider.CreateTileProviderForBinding(binding),
                null);
        }

        // A routed publication names a distinct connection. A provider singleton that also
        // implements ITileProvider is still bound to the primary/default connection, so using it
        // here would silently cross the source boundary. Only an explicitly binding-aware tile
        // provider is safe on this path.
        return new TileProviderResolution(null, binding.Provider.ProviderName);
    }

    // A routed publication without a binding-aware tile provider must advertise the raster path instead of native vector tiles.
    public async Task<bool> SupportsVectorTilesAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (!RequiresRouting(snapshot, publication))
        {
            return true;
        }

        var binding = await RequireRouter().ResolveBindingAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        return binding.Provider is IBindableTileProvider;
    }

    public static bool RequiresRouting(MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);

        var binding = snapshot.ResolveStorageBinding(publication);
        return binding is not null
            ? !string.IsNullOrWhiteSpace(binding.ConnectionId)
            : !string.IsNullOrWhiteSpace(publication.StorageBindingId);
    }

    private FeatureProviderQueryRouter RequireRouter()
    {
        if (_providerQueryRouter is null)
        {
            throw new InvalidOperationException("Feature provider routing is not configured.");
        }

        return _providerQueryRouter;
    }
}

public readonly record struct TileProviderResolution(ITileProvider? Provider, string? UnsupportedProviderName);

public readonly record struct TileFeatureReaderResolution(IFeatureReader Reader, int? StorageSrid);
