// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

internal static class AdminLayerConfigurationTargets
{
    private const int MetadataMutationMaxAttempts = 5;

    internal static MetadataV2StorageResourceResolution Resolve(MetadataV2GraphSnapshot snapshot, int storageLayerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var target = snapshot.ResolveStorageResource(storageLayerId);
        return target.IsAmbiguous || target.Resource?.Type is MetadataV2ResourceType.FeatureDataset or MetadataV2ResourceType.Table
            ? target
            : default;
    }

    internal static async Task<MetadataV2StorageResourceResolution> WriteAsync(
        IMetadataV2GraphStore graphStore,
        int storageLayerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graphStore);
        ArgumentNullException.ThrowIfNull(mutate);

        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var target = Resolve(snapshot, storageLayerId);
            if (target.IsAmbiguous || target.Resource is null)
            {
                return target;
            }

            var resources = snapshot.Graph.Resources.ToArray();
            var mutated = false;
            for (var i = 0; i < resources.Length; i++)
            {
                if (!string.Equals(resources[i].Metadata.Id, target.Resource.Metadata.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                resources[i] = mutate(resources[i]);
                mutated = true;
            }

            if (!mutated)
            {
                return target;
            }

            var updated = snapshot.Graph with
            {
                Resources = resources,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return target;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply.
            }
        }
    }

    private static bool IsEtagMismatch(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);
}
