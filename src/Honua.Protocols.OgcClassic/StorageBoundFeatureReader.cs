// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// Reader selected for one publication, plus whether the caller should keep the injected store.
/// </summary>
/// <param name="Reader">Reader returned by the provider router, or the injected reader when routing does not apply.</param>
/// <param name="UseInjectedReader">
/// True when the read must stay on the injected store: no binding, no router, or the binding
/// targets the managed features table.
/// </param>
internal readonly record struct StorageBoundFeatureRead(IFeatureReader Reader, bool UseInjectedReader);

/// <summary>
/// Resolves the feature reader for a classic OGC publication the same way OGC API Features does.
/// </summary>
internal static class StorageBoundFeatureReader
{
    /// <summary>
    /// Routes a publication that carries a storage binding through
    /// <see cref="FeatureProviderQueryRouter.ResolveReaderAsync"/>. Falls back to
    /// <paramref name="fallback"/> when the router is absent, the snapshot or service is
    /// missing, or the publication has no storage binding.
    /// </summary>
    /// <remarks>
    /// The injected reader is the managed <c>honua.features</c> store. It implements GML,
    /// GeoJSON, and raster-point reads; <c>PostgresStorageMappedFeatureReader</c> does not.
    /// A binding onto that table still resolves through the router, then keeps the injected
    /// reader so those rows and their encoded formats stay on the existing path. A
    /// source-backed table, or any table other than the managed features table, is read
    /// through the resolved reader.
    /// </remarks>
    internal static async ValueTask<StorageBoundFeatureRead> ResolveAsync(
        IFeatureReader fallback,
        FeatureProviderQueryRouter? router,
        MetadataV2GraphSnapshot? snapshot,
        MetadataV2Service? service,
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken)
    {
        if (router is null || snapshot is null || service is null || publication is null
            || string.IsNullOrEmpty(publication.StorageBindingId))
        {
            return new StorageBoundFeatureRead(fallback, UseInjectedReader: true);
        }

        var binding = snapshot.ResolveStorageBinding(publication);
        var managed = TargetsManagedFeatureTable(resource, binding);
        var reader = await router.ResolveReaderAsync(
                snapshot,
                service,
                resource,
                publication,
                storageLayerId,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
        return new StorageBoundFeatureRead(reader, managed);
    }

    /// <summary>
    /// True when the binding names the shared managed features table and is not source-backed.
    /// </summary>
    internal static bool TargetsManagedFeatureTable(
        MetadataV2Resource resource,
        MetadataV2StorageBinding? binding)
    {
        if (binding is null)
        {
            return false;
        }

        try
        {
            var mapping = FeatureStorageMapping.FromMetadata(resource, binding);
            return !mapping.IsSourceBacked
                && string.Equals(
                    mapping.TableName,
                    FeatureStorageMapping.ManagedFeaturesTableName,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
