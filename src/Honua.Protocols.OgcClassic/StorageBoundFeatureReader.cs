// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.Ogc.Classic;

internal readonly record struct StorageBoundFeatureRead(IFeatureReader Reader, bool UseInjectedReader);

internal static class StorageBoundFeatureReader
{
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
