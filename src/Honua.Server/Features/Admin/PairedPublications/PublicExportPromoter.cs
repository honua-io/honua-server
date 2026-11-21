// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicExportPromotionInput(
    PublicationDescriptor Descriptor,
    PairedPublicationStore PrivateStore,
    MetadataV2Object Resource,
    MetadataV2Service Service,
    int StorageLayerId,
    int LayerIndex,
    long MetadataRevision);

// Copies an eligible Export version to the public data path:
// artifacts -> version collection.json -> root catalog.json (pointer last), then checks the canary URL.
internal sealed class PublicExportPromoter(
    ICloudFileStorage privateStorage,
    PublicExportEligibility eligibility,
    IPublicExportStateStore stateStore,
    IPublicExportCanary canary,
    IPublicExportOpsNotifier opsNotifier,
    IPublicPublicationDestination? destination = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal const string NotConfiguredMessage = "public data is not configured";

    public async Task<PublicExportDeliveryStatus> PromoteAsync(
        PublicExportPromotionInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (destination is null)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.NotConfigured)
            {
                Reason = NotConfiguredMessage
            };
        }

        var check = await eligibility.EvaluateAsync(input.Resource, input.Service, cancellationToken).ConfigureAwait(false);
        if (!check.IsEligible)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.StaysSignedIn)
            {
                Reason = check.Reason
            };
        }

        var provenance = new PublicPublicationProvenance(
            input.Service.EffectivePublicName, input.LayerIndex, input.MetadataRevision);

        await CopyArtifactsAsync(input, provenance, cancellationToken).ConfigureAwait(false);

        var collectionUrl = await CopyFileAsync(
            input,
            ExportCollectionDocument.FileName,
            ExportCollectionDocument.MediaType,
            knownSha256: null,
            provenance,
            cancellationToken).ConfigureAwait(false);

        var catalogUrl = new Uri(destination.Origin, PublicPublicationDestination.RootCatalogKey);
        var record = new PromotedExportRecord
        {
            PublicationId = input.Descriptor.PublicationId,
            Version = input.Descriptor.Version,
            ServiceId = input.Service.Metadata.Id,
            ResourceId = input.Resource.Metadata.Id,
            StorageLayerId = input.StorageLayerId,
            Title = input.Descriptor.Title ?? input.Resource.Metadata.Title ?? input.Descriptor.PublicationId,
            CollectionUrl = collectionUrl.AbsoluteUri,
            CatalogUrl = catalogUrl.AbsoluteUri,
            PromotedAt = _timeProvider.GetUtcNow(),
            CanaryState = PublicExportCanaryState.Pending
        };

        await stateStore.SavePromotedAsync(record, cancellationToken).ConfigureAwait(false);
        var allPromoted = await stateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false);
        await WriteRootCatalogAsync(allPromoted, cancellationToken).ConfigureAwait(false);

        var canaryResult = await canary.CheckAsync(collectionUrl, cancellationToken).ConfigureAwait(false);
        var verified = record with
        {
            CanaryState = canaryResult.IsReadable ? PublicExportCanaryState.Readable : PublicExportCanaryState.Unreadable,
            CanaryDetail = canaryResult.Detail
        };
        await stateStore.SavePromotedAsync(verified, cancellationToken).ConfigureAwait(false);

        if (!canaryResult.IsReadable)
        {
            await opsNotifier.NotifyCanaryFailedAsync(
                input.Descriptor.PublicationId,
                input.Descriptor.Version,
                collectionUrl,
                canaryResult.Detail ?? "Public canary request failed.",
                cancellationToken).ConfigureAwait(false);

            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.PromotedUnreadable)
            {
                CollectionUrl = collectionUrl.AbsoluteUri,
                CatalogUrl = catalogUrl.AbsoluteUri,
                Reason = $"{OpsNotificationPublicExportNotifier.CanaryFindingTitle}: {canaryResult.Detail}"
            };
        }

        return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.Promoted)
        {
            CollectionUrl = collectionUrl.AbsoluteUri,
            CatalogUrl = catalogUrl.AbsoluteUri
        };
    }

    private async Task CopyArtifactsAsync(
        PublicExportPromotionInput input,
        PublicPublicationProvenance provenance,
        CancellationToken cancellationToken)
    {
        var descriptor = input.Descriptor;
        await CopyFileAsync(
            input,
            descriptor.Artifacts.Pmtiles.Href,
            PmtilesPublicationArtifact.MediaTypeValue,
            descriptor.Artifacts.Pmtiles.Sha256,
            provenance,
            cancellationToken).ConfigureAwait(false);

        await CopyFileAsync(
            input,
            descriptor.Artifacts.Geoparquet.Href,
            GeoparquetPublicationArtifact.MediaTypeValue,
            descriptor.Artifacts.Geoparquet.Sha256,
            provenance,
            cancellationToken).ConfigureAwait(false);

        foreach (var companion in ExportCompanionFiles.FileNames)
        {
            var privateKey = input.PrivateStore.VersionKey(descriptor.Version, companion);
            var metadata = await privateStorage.GetMetadataAsync(privateKey, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                continue;
            }

            await CopyFileAsync(
                input,
                companion,
                CompanionContentType(companion, metadata.ContentType),
                knownSha256: null,
                provenance,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> CopyFileAsync(
        PublicExportPromotionInput input,
        string fileName,
        string contentType,
        string? knownSha256,
        PublicPublicationProvenance provenance,
        CancellationToken cancellationToken)
    {
        var privateKey = input.PrivateStore.VersionKey(input.Descriptor.Version, fileName);
        var stream = await privateStorage.DownloadAsync(privateKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Private publication artifact '{fileName}' could not be read for public promotion.");

        await using (stream.ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(knownSha256))
            {
                return await destination!.PutAsync(
                    new PublicPublicationArtifact(
                        new PublicPublicationArtifactKey(input.Descriptor.PublicationId, input.Descriptor.Version, fileName),
                        stream,
                        contentType)
                    {
                        Sha256 = knownSha256,
                        CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                        Provenance = provenance
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            var bytes = buffered.ToArray();
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var uploadStream = new MemoryStream(bytes, writable: false);
            return await destination!.PutAsync(
                new PublicPublicationArtifact(
                    new PublicPublicationArtifactKey(input.Descriptor.PublicationId, input.Descriptor.Version, fileName),
                    uploadStream,
                    contentType)
                {
                    SizeBytes = bytes.Length,
                    Sha256 = sha256,
                    CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                    Provenance = provenance
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteRootCatalogAsync(
        IReadOnlyList<PromotedExportRecord> promoted,
        CancellationToken cancellationToken)
    {
        var bytes = PublicExportCatalogDocument.Serialize(promoted);
        using var stream = new MemoryStream(bytes, writable: false);
        await destination!.PutRootCatalogAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static string CompanionContentType(string fileName, string? fallback)
        => fileName switch
        {
            ExportCompanionFiles.ReadmeFileName or ExportCompanionFiles.AgentsFileName => ExportCompanionFiles.MarkdownMediaType,
            ExportCompanionFiles.StyleFileName => ExportCompanionFiles.StyleMediaType,
            ExportThumbnail.FileName => ExportThumbnail.MediaType,
            _ => string.IsNullOrWhiteSpace(fallback) ? "application/octet-stream" : fallback
        };
}
