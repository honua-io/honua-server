// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.PairedPublications;

// The caller holds the source/access fence through promotion and tracks every copied generation
// for origin deletion and CDN invalidation when access is reduced.
internal interface IPublicPublicationDestination
{
    Task<Uri> PutAsync(PublicPublicationArtifact artifact, CancellationToken cancellationToken);

    // Idempotent at the origin: an already absent artifact succeeds; inability to confirm deletion fails.
    // CDN invalidation is a separate caller responsibility.
    Task DeleteAsync(PublicPublicationArtifactKey key, CancellationToken cancellationToken);
}

internal sealed record PublicPublicationArtifactKey(string PublicationId, string Version, string FileName);

internal sealed record PublicPublicationArtifact(PublicPublicationArtifactKey Key, Stream Content, string ContentType)
{
    public long? SizeBytes { get; init; }

    public PublicPublicationProvenance? Provenance { get; init; }
}

// Resolved public route identity and the metadata activation revision from the same source snapshot.
// MetadataRevision is not a release-train pin or the captured data revision; paired exports select no style.
internal sealed record PublicPublicationProvenance(string ServiceId, int LayerIndex, long MetadataRevision);
