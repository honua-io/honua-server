// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.PairedPublications;

// One routable managed layer and the change fence that proves both artifacts read the same data.
internal sealed class PairedPublicationSource
{
    private readonly IChangeTracker _changeTracker;

    private PairedPublicationSource(
        PMTilesPublishedSource published,
        int storageLayerId,
        long metadataRevision,
        long generation,
        IChangeTracker changeTracker)
    {
        Published = published;
        StorageLayerId = storageLayerId;
        MetadataRevision = metadataRevision;
        Generation = generation;
        _changeTracker = changeTracker;
    }

    public PMTilesPublishedSource Published { get; }

    public MetadataV2Resource Resource => Published.Resource;

    public int StorageLayerId { get; }

    public long MetadataRevision { get; }

    public long Generation { get; }

    public string Revision => string.Create(
        CultureInfo.InvariantCulture, $"metadata-{MetadataRevision}:changes-{Generation}");

    public static async Task<PairedPublicationSource> OpenAsync(
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider services,
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var published = PMTilesProxySourceResolver.ResolveForPublish(snapshot, serviceId, layerId)
            ?? throw new PairedPublicationException("The paired publish target is not a routable layer of the service.");

        if (!IsManagedTable(published.Resource, snapshot.ResolveStorageBinding(published.Publication)))
        {
            throw new PairedPublicationException(
                "Paired publish needs a layer stored in Honua's managed feature table, whose edits it can fence.");
        }

        var changeTracker = services.GetService<IChangeTracker>();
        if (changeTracker is null or NoOpChangeTracker)
        {
            throw new PairedPublicationException("Paired publish needs edit change tracking, which this data provider does not record.");
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(published.Publication)
            ?? snapshot.ResolveStorageLayerId(published.Resource)
            ?? layerId;
        var generation = await changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        return new PairedPublicationSource(published, storageLayerId, snapshot.Revision, generation, changeTracker);
    }

    private static bool IsManagedTable(MetadataV2Resource resource, MetadataV2StorageBinding? binding)
    {
        // A binding on its own connection is not the database whose edit log the fence reads.
        if (binding is null || !string.IsNullOrWhiteSpace(binding.ConnectionId))
        {
            return false;
        }

        try
        {
            var mapping = FeatureStorageMapping.FromMetadata(resource, binding);
            return !mapping.IsSourceBacked
                && FeatureStorageMapping.ManagedFeaturesTableName.Equals(mapping.TableName, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<IFeatureReader> ResolveReaderAsync(
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var router = services.GetService<FeatureProviderQueryRouter>();
        if (router is null)
        {
            return services.GetRequiredService<IFeatureReader>();
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await router.ResolveReaderAsync(
            snapshot,
            Published.Service,
            Published.Resource,
            Published.Publication,
            StorageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
    }

    // Unchanged metadata and no committed edit to the layer since the fence opened.
    public async Task<bool> IsUnchangedAsync(IMetadataV2GraphProvider graphProvider, CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Revision != MetadataRevision)
        {
            return false;
        }

        return !await _changeTracker.HasLayerChangedSinceAsync(Generation, StorageLayerId, cancellationToken).ConfigureAwait(false);
    }
}

internal class PairedPublicationException(string message) : Exception(message);

// The pointer may name the version, so the version must be kept.
internal sealed class PairedActivationUnknownException(string message) : PairedPublicationException(message);
