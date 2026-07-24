// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Api.Tiles.Services;

/// <summary>
/// Resolves OGC API Tiles raster/vector tile operations through the provider selected by a
/// layer's Metadata v2 storage binding (honua-server#2962). A publication only routes away
/// from the primary reader/tile provider when its resolved storage binding carries a
/// connection id.
/// </summary>
internal sealed class TileFeatureProviderResolver(FeatureProviderQueryRouter? providerQueryRouter)
{
    private readonly FeatureProviderQueryRouter? _providerQueryRouter = providerQueryRouter;

    /// <summary>
    /// Resolves the feature reader used for raster (PNG) tile rendering. Raster rendering only
    /// needs <see cref="IFeatureReader.QueryAsync"/>, which every provider implements, so raster
    /// tiles fully route to a secondary/additional provider. Falls back to the DI-registered
    /// primary reader when routing does not apply for this publication.
    /// </summary>
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

    /// <summary>
    /// Resolves the raster feature reader together with the physical storage SRID selected by
    /// its binding. Routed providers compare filters against, and return WKB in, this CRS even
    /// when the publication advertises a projected-on-read CRS.
    /// </summary>
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

    /// <summary>
    /// Resolves the tile provider used for vector (MVT) tile rendering. Falls back to the
    /// DI-registered primary tile provider when routing does not apply for this publication.
    /// Native MVT generation is a per-provider capability (only the PostGIS provider implements
    /// <see cref="ITileProvider"/> today); when routing resolves to a provider that does not
    /// implement it, <see cref="TileProviderResolution.UnsupportedProviderName"/> is set so the
    /// caller can fail loudly instead of silently falling back to the primary tile provider.
    /// </summary>
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

    /// <summary>
    /// Reports whether a collection can safely advertise native vector tiles. Routed
    /// publications require a binding-aware tile provider; otherwise their discoverable tile
    /// metadata must advertise the raster path that every query-capable provider supports.
    /// </summary>
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

    /// <summary>
    /// A publication only routes away from the primary reader/tile provider when its resolved
    /// storage binding carries a connection id. Resolution follows Metadata v2 semantics:
    /// publication binding, resource primary binding, then the resource's first binding. A
    /// resolved binding with no connection keeps using the primary pipeline byte-identically —
    /// the common case for default single-provider deployments.
    /// </summary>
    internal static bool RequiresRouting(MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
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

/// <summary>
/// Result of resolving the tile provider for a routed collection: either the provider to
/// render with, or the canonical name of a resolved provider that does not implement
/// <see cref="ITileProvider"/> so the caller can reject the request with a clear error.
/// </summary>
/// <param name="Provider">The resolved tile provider, or <see langword="null"/> when the
/// routed provider does not support tile generation.</param>
/// <param name="UnsupportedProviderName">Canonical name of the resolved provider when it does
/// not implement <see cref="ITileProvider"/>; <see langword="null"/> otherwise.</param>
internal readonly record struct TileProviderResolution(ITileProvider? Provider, string? UnsupportedProviderName);

/// <summary>
/// Raster reader resolution, including the physical SRID used by the selected storage binding.
/// </summary>
/// <param name="Reader">Resolved feature reader.</param>
/// <param name="StorageSrid">Physical storage SRID, when declared by metadata.</param>
internal readonly record struct TileFeatureReaderResolution(IFeatureReader Reader, int? StorageSrid);
