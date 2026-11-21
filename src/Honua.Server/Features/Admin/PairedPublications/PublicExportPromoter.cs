// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Infrastructure.SharedCaching;

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

    public required IMetadataV2GraphProvider GraphProvider { get; init; }

    public required PublicExportRevocationService Revocation { get; init; }

    public required IPublicExportStateStore StateStore { get; init; }

    public required IPublicExportCanary Canary { get; init; }

    public required IPublicExportOpsNotifier OpsNotifier { get; init; }
}

// Copies an eligible Export version to the public data path:
// artifacts -> version collection.json -> root catalog.json (pointer last), then checks the canary URL.
internal sealed class PublicExportPromoter(
    PublicExportPromoterDependencies? deps = null,
    IPublicPublicationDestination? destination = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal const string NotConfiguredMessage = "public data is not configured";

    private sealed record ArtifactCopySpec
    {
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public string? KnownSha256 { get; init; }
        public long? KnownSizeBytes { get; init; }
        public required PublicPublicationProvenance Provenance { get; init; }
    }

    public async Task<PublicExportDeliveryStatus> PromoteAsync(
        PublicExportPromotionInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (destination is null || deps is null)
        {
            return new PublicExportDeliveryStatus(PublicExportDeliveryStatus.NotConfigured) { Reason = NotConfiguredMessage };
        }

        var capturedCheck = await deps.Eligibility.EvaluateAsync(input.Resource, input.Service, cancellationToken).ConfigureAwait(false);
        if (!capturedCheck.IsEligible)
        {
            return StaysSignedIn(capturedCheck.Reason);
        }

        var currentCheck = await EvaluateCurrentSnapshotAsync(input, cancellationToken).ConfigureAwait(false);
        if (!currentCheck.IsEligible)
        {
            return StaysSignedIn(currentCheck.Reason);
        }

        var provenance = new PublicPublicationProvenance(
            input.Service.EffectivePublicName, input.LayerIndex, input.MetadataRevision);
        await CopyArtifactsAsync(input, provenance, cancellationToken).ConfigureAwait(false);
        var collectionUrl = await CopyCollectionAsync(input, provenance, cancellationToken).ConfigureAwait(false);

        var record = BuildPromotedRecord(input, collectionUrl);
        await deps.StateStore.SavePromotedAsync(record, cancellationToken).ConfigureAwait(false);
        var allPromoted = await deps.StateStore.ListPromotedAsync(cancellationToken).ConfigureAwait(false);
        await WriteRootCatalogAsync(allPromoted, cancellationToken).ConfigureAwait(false);

        var lateCheck = await EvaluateCurrentSnapshotAsync(input, cancellationToken).ConfigureAwait(false);
        if (!lateCheck.IsEligible)
        {
            return await RevokeAfterLateIneligibilityAsync(input.Descriptor.PublicationId, lateCheck.Reason, cancellationToken)
                .ConfigureAwait(false);
        }

        return await VerifyCanaryAsync(record, collectionUrl, cancellationToken).ConfigureAwait(false);
    }

    private static PublicExportDeliveryStatus StaysSignedIn(string? reason)
        => new(PublicExportDeliveryStatus.StaysSignedIn) { Reason = reason };

    private Task<Uri> CopyCollectionAsync(
        PublicExportPromotionInput input,
        PublicPublicationProvenance provenance,
        CancellationToken cancellationToken)
        => CopyFileAsync(
            input,
            new ArtifactCopySpec
            {
                FileName = ExportCollectionDocument.FileName,
                ContentType = ExportCollectionDocument.MediaType,
                Provenance = provenance
            },
            cancellationToken);

    private async Task<PublicExportDeliveryStatus> RevokeAfterLateIneligibilityAsync(
        string publicationId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await deps!.Revocation.RequestRevocationAsync(
            new PublicExportRevocationRequest(
                publicationId,
                PublicExportRevocationService.ReasonAccessRestricted,
                CdnPurgeCause.NoRequest),
            cancellationToken).ConfigureAwait(false);
        return StaysSignedIn(reason);
    }

    private async Task<PublicExportEligibilityResult> EvaluateCurrentSnapshotAsync(
        PublicExportPromotionInput input,
        CancellationToken cancellationToken)
    {
        var snapshot = await deps!.GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await deps.Eligibility.EvaluateSnapshotAsync(
            snapshot, input.Service.Metadata.Id, input.Resource.Metadata.Id, cancellationToken).ConfigureAwait(false);
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
        var canaryResult = await deps!.Canary.CheckAsync(collectionUrl, cancellationToken).ConfigureAwait(false);
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
            new PublicExportCanaryFailure
            {
                PublicationId = record.PublicationId,
                Version = record.Version,
                PublicUrl = collectionUrl,
                Detail = detail
            },
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
            new ArtifactCopySpec
            {
                FileName = descriptor.Artifacts.Pmtiles.Href,
                ContentType = PmtilesPublicationArtifact.MediaTypeValue,
                KnownSha256 = descriptor.Artifacts.Pmtiles.Sha256,
                KnownSizeBytes = descriptor.Artifacts.Pmtiles.Bytes,
                Provenance = provenance
            },
            cancellationToken).ConfigureAwait(false);

        await CopyFileAsync(
            input,
            new ArtifactCopySpec
            {
                FileName = descriptor.Artifacts.Geoparquet.Href,
                ContentType = GeoparquetPublicationArtifact.MediaTypeValue,
                KnownSha256 = descriptor.Artifacts.Geoparquet.Sha256,
                KnownSizeBytes = descriptor.Artifacts.Geoparquet.Bytes,
                Provenance = provenance
            },
            cancellationToken).ConfigureAwait(false);

        await CopyCompanionsAsync(input, provenance, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyCompanionsAsync(
        PublicExportPromotionInput input,
        PublicPublicationProvenance provenance,
        CancellationToken cancellationToken)
    {
        foreach (var companion in ExportCompanionFiles.FileNames)
        {
            var metadata = await deps!.PrivateStorage.GetMetadataAsync(
                input.PrivateStore.VersionKey(input.Descriptor.Version, companion), cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                continue;
            }

            await CopyFileAsync(
                input,
                new ArtifactCopySpec
                {
                    FileName = companion,
                    ContentType = CompanionContentType(companion, metadata.ContentType),
                    Provenance = provenance
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> CopyFileAsync(
        PublicExportPromotionInput input,
        ArtifactCopySpec spec,
        CancellationToken cancellationToken)
    {
        var privateKey = input.PrivateStore.VersionKey(input.Descriptor.Version, spec.FileName);
        var stream = await deps!.PrivateStorage.DownloadAsync(privateKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Private publication artifact '{spec.FileName}' could not be read for public promotion.");

        await using (stream.ConfigureAwait(false))
        {
            var artifactKey = new PublicPublicationArtifactKey(input.Descriptor.PublicationId, input.Descriptor.Version, spec.FileName);
            if (!string.IsNullOrWhiteSpace(spec.KnownSha256))
            {
                return await destination!.PutAsync(
                    new PublicPublicationArtifact(artifactKey, stream, spec.ContentType)
                    {
                        SizeBytes = spec.KnownSizeBytes,
                        Sha256 = spec.KnownSha256,
                        CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                        Provenance = spec.Provenance
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return await UploadBufferedAsync((artifactKey, spec), stream, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Uri> UploadBufferedAsync(
        (PublicPublicationArtifactKey Key, ArtifactCopySpec Spec) target,
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        var bytes = buffered.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var uploadStream = new MemoryStream(bytes, writable: false);
        return await destination!.PutAsync(
            new PublicPublicationArtifact(target.Key, uploadStream, target.Spec.ContentType)
            {
                SizeBytes = bytes.Length,
                Sha256 = sha256,
                CachePolicy = StoredObjectCachePolicy.PublicImmutable,
                Provenance = target.Spec.Provenance
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
