// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed class PairedPublicationReader(
    ICloudFileStorage storage,
    IOptions<CloudStorageOptions> options,
    IMetadataV2GraphProvider graphProvider)
{
    internal async Task<PairedPublicationReadTarget?> ResolveCurrentAsync(
        string publicationId, CancellationToken cancellationToken)
    {
        if (!PairedPublishRequestRules.IsPublicationId(publicationId))
        {
            return null;
        }

        var store = PairedPublicationStore.Create(storage, options.Value, publicationId);
        try
        {
            var pointer = await store.ReadPointerAsync(cancellationToken).ConfigureAwait(false);
            if (pointer is null || pointer.PublicationId != publicationId || !IsVersion(pointer.Version) ||
                pointer.Descriptor != $"{pointer.Version}/descriptor.json")
            {
                return null;
            }

            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var current = await ResolveVersionAsync(store, pointer.Version, snapshot, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Descriptor.PublicationId != publicationId)
            {
                return null;
            }
            return current;
        }
        catch (JsonException)
        {
            // Invalid or oversized control documents never resolve to artifact keys.
            return null;
        }
    }

    internal async Task<PairedPublicationReadTarget?> ResolvePinnedAsync(
        PairedPublicationReadTarget current, string version, CancellationToken cancellationToken)
    {
        if (!IsVersion(version))
        {
            return null;
        }
        if (version == current.Descriptor.Version)
        {
            return current;
        }
        try
        {
            var pinned = await ResolveVersionAsync(current.Store, version, current.Snapshot, cancellationToken).ConfigureAwait(false);
            return pinned?.Descriptor.PublicationId == current.Descriptor.PublicationId &&
                PMTilesProxySourceResolver.SameSource(current.Source, pinned.Source) ? pinned : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<PairedPublicationReadTarget?> ResolveVersionAsync(
        PairedPublicationStore store, string version, MetadataV2GraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        var descriptor = await store.ReadVersionAsync(version, cancellationToken).ConfigureAwait(false);
        if (!IsDescriptor(descriptor, version))
        {
            return null;
        }

        var pmtiles = await storage.GetMetadataAsync(
            store.VersionKey(version, descriptor!.Artifacts.Pmtiles.Href), cancellationToken).ConfigureAwait(false);
        // Paired publish always writes the full source stamp. Never use the legacy layer-only fallback.
        if (pmtiles is null || !HasSourceStamp(pmtiles))
        {
            return null;
        }
        var source = PMTilesProxySourceResolver.Resolve(snapshot, pmtiles.Metadata);
        if (source is null || source.Service.Metadata.Id != descriptor.SourceLayer.Service ||
            source.Resource.Metadata.Id != descriptor.SourceLayer.Layer ||
            (!string.IsNullOrWhiteSpace(PMTilesProxySourceResolver.BindingId(source)) &&
                !HasStampValue(pmtiles, PMTilesProxySourceResolver.StorageBindingIdMetadataKey)))
        {
            return null;
        }

        var fields = descriptor.Artifacts.Pmtiles.Fields.Concat(descriptor.Artifacts.Geoparquet.Fields);
        if (fields.Any(name => !source.Resource.SchemaFields.Any(field =>
            !field.Hidden && string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }
        return new(store, descriptor, source, snapshot);
    }

    internal Task<CloudFile?> GetFileAsync(PairedPublicationReadTarget target, string fileName, CancellationToken cancellationToken)
        => storage.GetMetadataAsync(target.Store.VersionKey(target.Descriptor.Version, fileName), cancellationToken);

    internal static bool IsAllowedFile(PublicationDescriptor descriptor, string fileName)
        => fileName is "descriptor.json" or "item.json" ||
           fileName == descriptor.Artifacts.Pmtiles.Href || fileName == descriptor.Artifacts.Geoparquet.Href;

    private static bool IsDescriptor(PublicationDescriptor? descriptor, string version)
        => descriptor is { SourceLayer: not null, Artifacts.Pmtiles.Fields: not null, Artifacts.Geoparquet.Fields: not null } &&
           descriptor.Version == version && PairedPublishRequestRules.IsPublicationId(descriptor.PublicationId) &&
           descriptor.Validation?.Status == PublicationValidation.Passed &&
           descriptor.Artifacts.Pmtiles.Href == $"{descriptor.PublicationId}.pmtiles" &&
           descriptor.Artifacts.Geoparquet.Href == $"{descriptor.PublicationId}.parquet";

    private static bool HasSourceStamp(CloudFile file)
        => HasStampValue(file, PMTilesProxySourceResolver.LayerIdMetadataKey) &&
           HasStampValue(file, PMTilesProxySourceResolver.ServiceIdMetadataKey) &&
           HasStampValue(file, PMTilesProxySourceResolver.ResourceIdMetadataKey) &&
           HasStampValue(file, PMTilesProxySourceResolver.PublicationIdMetadataKey);

    private static bool HasStampValue(CloudFile file, string name)
        => file.Metadata.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value));

    private static bool IsVersion(string? value)
        => value is { Length: > 0 and <= 96 } && char.IsAsciiLetterOrDigit(value[0]) &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

internal sealed record PairedPublicationReadTarget(
    PairedPublicationStore Store, PublicationDescriptor Descriptor, PMTilesPublishedSource Source, MetadataV2GraphSnapshot Snapshot);
