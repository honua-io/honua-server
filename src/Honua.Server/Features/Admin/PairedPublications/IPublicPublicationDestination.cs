// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Admin.PairedPublications;

// The caller holds the source/access fence through promotion and tracks every copied generation
// for origin deletion and CDN invalidation when access is reduced.
internal interface IPublicPublicationDestination
{
    Uri Origin { get; }

    Task<Uri> PutAsync(PublicPublicationArtifact artifact, CancellationToken cancellationToken);

    Task<Uri> PutRootCatalogAsync(Stream content, CancellationToken cancellationToken);

    // Idempotent at the origin: an already absent artifact succeeds; inability to confirm deletion fails.
    // CDN invalidation is a separate caller responsibility.
    Task DeleteAsync(PublicPublicationArtifactKey key, CancellationToken cancellationToken);

    // Deletes every public object under data/publications/{publicationId}/, confirming absence.
    Task DeletePublicationAsync(string publicationId, CancellationToken cancellationToken);
}

internal sealed record PublicPublicationArtifactKey(string PublicationId, string Version, string FileName);

internal sealed record PublicPublicationArtifact(PublicPublicationArtifactKey Key, Stream Content, string ContentType)
{
    public long? SizeBytes { get; init; }

    public string? Sha256 { get; init; }

    public StoredObjectCachePolicy CachePolicy { get; init; } = StoredObjectCachePolicy.PublicImmutable;

    public PublicPublicationProvenance? Provenance { get; init; }
}

// Resolved public route identity and the metadata activation revision from the same source snapshot.
// MetadataRevision is not a release-train pin or the captured data revision; paired exports select no style.
internal sealed record PublicPublicationProvenance(string ServiceId, int LayerIndex, long MetadataRevision);
