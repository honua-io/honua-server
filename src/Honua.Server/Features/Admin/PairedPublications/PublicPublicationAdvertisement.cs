// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicPublicationReadRequest(
    PublicPublicationKey Key, PublicPublicationSourceIdentity Source, string FileName);

internal sealed class PublicPublicationAdvertisement(
    IPublicPublicationJournal journal, PublicPublicationSourceGuard guard, PublicPublicationCleanupTarget target)
{
    internal async Task<Uri?> ResolveAsync(PublicPublicationReadRequest request, CancellationToken cancellationToken)
    {
        await using var session = await journal.OpenAsync(request.Key, cancellationToken).ConfigureAwait(false);
        var generation = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null || generation.State != PublicPublicationState.Ready ||
            !Matches(generation.Reservation, request.Source) ||
            !await guard.IsCurrentAndEligibleAsync(request.Source, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var key = new PublicPublicationArtifactKey(request.Key.PublicationId, request.Key.Version, request.FileName);
        return new Uri(new Uri(target.BaseUrl), PublicPublicationDestination.ObjectKey(key));
    }

    private bool Matches(PublicPublicationReservation reservation, PublicPublicationSourceIdentity source) =>
        reservation.MetadataRevision == source.MetadataRevision &&
        reservation.LayerIndex == source.LayerIndex &&
        string.Equals(reservation.ServiceId, source.Source.Service.EffectivePublicName, StringComparison.Ordinal) &&
        string.Equals(reservation.DestinationBucket, target.Bucket, StringComparison.Ordinal) &&
        string.Equals(reservation.DestinationBaseUrl, target.BaseUrl, StringComparison.Ordinal) &&
        string.Equals(reservation.CdnDistributionId, target.DistributionId, StringComparison.Ordinal);
}
