// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicExportPromotionInput
{
    public required PublicationDescriptor Descriptor { get; init; }

    public required PairedPublicationStore PrivateStore { get; init; }

    public required MetadataV2Object Resource { get; init; }

    public required MetadataV2Service Service { get; init; }

    public int StorageLayerId { get; init; }

    public int LayerIndex { get; init; }

    public long MetadataRevision { get; init; }
}

internal sealed record PublicExportPromoterDependencies
{
    public required ICloudFileStorage PrivateStorage { get; init; }

    public required PublicExportEligibility Eligibility { get; init; }

    public required IPublicExportStateStore StateStore { get; init; }

    public required IPublicExportCanary Canary { get; init; }

    public required IPublicExportOpsNotifier OpsNotifier { get; init; }
}

// Copies an eligible Export version to the public data path:
// artifacts -> version collection.json -> root catalog.json (pointer last), then checks the canary URL.
internal sealed class PublicExportPromoter(
    PublicExportPromoterDependencies deps,
    IPublicPublicationDestination? destination = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal const string NotConfiguredMessage = "public data is not configured";

    private sealed record ArtifactCopySpec(
        string FileName,
        string ContentType,
        string? KnownSha256,
        PublicPublicationProvenance Provenance);

    public async Task<PublicExportDeliveryStatus> PromoteAsync(
        PublicExportPromotionInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (destination is null)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.NotConfigured) { Reason = NotConfiguredMessage };
        }

        var check = await deps.Eligibility.EvaluateAsync(input.Resource, input.Service, cancellationToken).ConfigureAwait(false);
        if (!check.IsEligible)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.StaysSignedIn) { Reason = check.Reason };
        }

        var provenance = new PublicPublicationProvenance(
            input.Service.EffectivePublicName, input.LayerIndex, input.MetadataRevision);
        await CopyArtifactsAsync(input, provenance, cancellationToken).ConfigureAwait(false);

        var collectionUrl = await CopyFileAsync(
            input,
            new ArtifactCopySpec(ExportCollectionDocument.FileName, ExportCollectionDocument.MediaType, null, provenance),
            cancellationToken).ConfigureAwait(false);

        var record = BuildPromotedRecord(input, collectionUrl);
        await deps.StateStore.SavePromotedAsync(record, cancellationToken).ConfigureAwait(false);
        var allPromoted = await deps.StateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false);
        await WriteRootCatalogAsync(allPromoted, cancellationToken).ConfigureAwait(false);

        return await VerifyCanaryAsync(record, collectionUrl, cancellationToken).ConfigureAwait(false);
    }

    private PromotedExportRecord BuildPromotedRecord(PublicExportPromotionInput input, Uri collectionUrl)
    {
        var catalogUrl = new Uri(destination!.Origin, PublicPublicationDestination.RootCatalogKey);
        return new PromotedExportRecord
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
    }

    private async Task<PublicExportDeliveryStatus> VerifyCanaryAsync(
        PromotedExportRecord record,
        Uri collectionUrl,
        CancellationToken cancellationToken)
    {
        var canaryResult = await deps.Canary.CheckAsync(collectionUrl, cancellationToken).ConfigureAwait(false);
        var verified = record with
        {
            CanaryState = canaryResult.IsReadable ? PublicExportCanaryState.Readable : PublicExportCanaryState.Unreadable,
            CanaryDetail = canaryResult.Detail
        };
        await deps.StateStore.SavePromotedAsync(verified, cancellationToken).ConfigureAwait(false);

        if (canaryResult.IsReadable)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.Promoted)
            {
                CollectionUrl = record.CollectionUrl,
                CatalogUrl = record.CatalogUrl
            };
        }

        var detail = canaryResult.Detail ?? "Public canary request failed.";
        await deps.OpsNotifier.NotifyCanaryFailedAsync(
            new PublicExportCanaryFailure(record.PublicationId, record.Version, collectionUrl, detail),
            cancellationToken).ConfigureAwait(false);

        return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.PromotedUnreadable)
        {
            CollectionUrl = record.CollectionUrl,
            CatalogUrl = record.CatalogUrl,
            Reason = $"{OpsNotificationPublicExportNotifier.CanaryFindingTitle}: {detail}"
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
            new ArtifactCopySpec(descriptor.Artifacts.Pmtiles.Href, PmtilesPublicationArtifact.MediaTypeValue,
                descriptor.Artifacts.Pmtiles.Sha256, provenance),
            cancellationToken).ConfigureAwait(false);

        await CopyFileAsync(
            input,
            new ArtifactCopySpec(descriptor.Artifacts.Geoparquet.Href, GeoparquetPublicationArtifact.MediaTypeValue,
                descriptor.Artifacts.Geoparquet.Sha256, provenance),
            cancellationToken).ConfigureAwait(false);

        foreach (var companion in ExportCompanionFiles.FileNames)
        {
            var metadata = await deps.PrivateStorage.GetMetadataAsync(
                input.PrivateStore.VersionKey(descriptor.Version, companion), cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                continue;
            }

            await CopyFileAsync(
                input,
                new ArtifactCopySpec(companion, CompanionContentType(companion, metadata.ContentType), null, provenance),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> CopyFileAsync(
        PublicExportPromotionInput input,
        ArtifactCopySpec spec,
        CancellationToken cancellationToken)
    {
        var privateKey = input.PrivateStore.VersionKey(input.Descriptor.Version, spec.FileName);
        var stream = await deps.PrivateStorage.DownloadAsync(privateKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Private publication artifact '{spec.FileName}' could not be read for public promotion.");

        await using (stream.ConfigureAwait(false))
        {
            var artifactKey = new PublicPublicationArtifactKey(input.Descriptor.PublicationId, input.Descriptor.Version, spec.FileName);
            if (!string.IsNullOrWhiteSpace(spec.KnownSha256))
            {
                return await destination!.PutAsync(
                    new PublicPublicationArtifact(artifactKey, stream, spec.ContentType)
                    {
                        Sha256 = spec.KnownSha256,
                        CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                        Provenance = spec.Provenance
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return await UploadBufferedAsync(artifactKey, stream, spec, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> UploadBufferedAsync(
        PublicPublicationArtifactKey artifactKey,
        Stream stream,
        ArtifactCopySpec spec,
        CancellationToken cancellationToken)
    {
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        var bytes = buffered.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var uploadStream = new MemoryStream(bytes, writable: false);
        return await destination!.PutAsync(
            new PublicPublicationArtifact(artifactKey, uploadStream, spec.ContentType)
            {
                SizeBytes = bytes.Length,
                Sha256 = sha256,
                CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                Provenance = spec.Provenance
            },
            cancellationToken).ConfigureAwait(false);
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
