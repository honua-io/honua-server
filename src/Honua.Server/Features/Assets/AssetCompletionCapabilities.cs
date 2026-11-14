// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Forms.Packages;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Assets;

internal sealed class AssetCompletionCapabilities(IFormTargetMetadataResolver targets, FeatureMutationEventService events, FeatureProviderQueryRouter providers)
{
    public async Task<AssetProfile> DescribeAsync(AssetProfile profile, CancellationToken cancellationToken)
    {
        var target = await targets.ResolveAsync(new FormTargetDefinition
        {
            ServiceId = profile.ServiceId,
            LayerId = profile.LayerId,
        }, cancellationToken).ConfigureAwait(false);
        return profile with
        {
            CompletionCapabilities = new AssetProfileCompletionCapabilities
            {
                MappedAssetWriteback = profile.State == AssetProfileState.Active && events.OutboxEnabled &&
                    await SupportsWritebackAsync(target, cancellationToken).ConfigureAwait(false),
            },
        };
    }

    public async Task<bool> SupportsWritebackAsync(FormTargetMetadataResolution target, CancellationToken cancellationToken)
    {
        if (target is not { Snapshot: { } snapshot, Service: { } service, Publication: { } publication,
            Resource: { } resource, StorageLayerId: int layerId })
        {
            return false;
        }

        try
        {
            var binding = await providers.ResolveBindingAsync(snapshot, service, resource, publication,
                layerId, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
            if (binding.Connection is not null || binding.Provider.Writer is null)
            {
                return false;
            }

            var reader = binding.Provider is IBindableFeatureDataProvider bindable
                ? bindable.CreateReaderForBinding(binding)
                : binding.Provider.Reader;
            return reader is IManagedFeatureWriteSupport managed &&
                await managed.SupportsManagedWritesAsync(layerId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
