// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicPublicationCleanupTarget
{
    public required string Bucket { get; init; }
    public required string BaseUrl { get; init; }
    public required string DistributionId { get; init; }
    public required IPublicPublicationDestination Destination { get; init; }
    public required ICdnInvalidator Invalidator { get; init; }
    public required ICdnInvalidationStatusReader StatusReader { get; init; }
}

internal sealed class PublicPublicationRevoker(IPublicPublicationJournal journal, PublicPublicationCleanupTarget target)
{
    public async Task<bool> RevokeAsync(PublicPublicationKey key, CancellationToken cancellationToken)
    {
        await using (var session = await journal.OpenAsync(key, cancellationToken).ConfigureAwait(false))
        {
            var generation = await session.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Public generation is not recorded.");
            RequireOriginalTarget(generation.Reservation);
            if (generation.State == PublicPublicationState.Revoked)
            {
                return true;
            }
            await session.BeginRevocationAsync(cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Revoking is durable before any origin mutation; an interrupted cleanup cannot reopen copying.
        var purge = await DeleteAndSubmitAsync(key, cancellationToken).ConfigureAwait(false);
        return await ConfirmCompletionAsync(key, purge, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublicPublicationPurge> DeleteAndSubmitAsync(PublicPublicationKey key, CancellationToken cancellationToken)
    {
        await using var session = await journal.OpenAsync(key, cancellationToken).ConfigureAwait(false);
        var generation = await RequireRevokingAsync(session, cancellationToken).ConfigureAwait(false);
        foreach (var name in new[] { $"{key.PublicationId}.pmtiles", $"{key.PublicationId}.parquet", "descriptor.json", "item.json" })
        {
            await target.Destination.DeleteAsync(new(key.PublicationId, key.Version, name), cancellationToken).ConfigureAwait(false);
        }
        await session.RecordOriginDeletedAsync(cancellationToken).ConfigureAwait(false);
        var ids = generation.PurgeIds;
        if (ids.Count == 0)
        {
            var path = $"/data/publications/{key.PublicationId}/{key.Version}/*";
            var submitted = await target.Invalidator.InvalidateAsync([path], cancellationToken).ConfigureAwait(false);
            if (submitted.Outcome != CdnInvalidationOutcome.Submitted || string.IsNullOrWhiteSpace(submitted.Detail))
            {
                throw new InvalidOperationException("Public generation purge has no provider receipts.");
            }
            ids = submitted.Detail.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("Public generation purge has no provider receipts.");
            }
            await session.RecordPurgeSubmittedAsync(new(target.DistributionId, ids), cancellationToken).ConfigureAwait(false);
        }
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(target.DistributionId, ids);
    }

    private async Task<bool> ConfirmCompletionAsync(PublicPublicationKey key, PublicPublicationPurge purge, CancellationToken cancellationToken)
    {
        await using var session = await journal.OpenAsync(key, cancellationToken).ConfigureAwait(false);
        _ = await RequireRevokingAsync(session, cancellationToken).ConfigureAwait(false);
        foreach (var id in purge.InvalidationIds)
        {
            var status = await target.StatusReader.ReadAsync(new(purge.DistributionId, id), cancellationToken).ConfigureAwait(false);
            if (status.Status != CdnInvalidationStatus.Completed)
            {
                return false;
            }
        }
        await session.RecordRevocationCompletedAsync(purge, cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<PublicPublicationGeneration> RequireRevokingAsync(IPublicPublicationSession session, CancellationToken cancellationToken)
    {
        var generation = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null || generation.State != PublicPublicationState.Revoking)
        {
            throw new InvalidOperationException("Public generation is no longer revoking.");
        }
        RequireOriginalTarget(generation.Reservation);
        return generation;
    }

    private void RequireOriginalTarget(PublicPublicationReservation reservation)
    {
        if (!string.Equals(reservation.DestinationBucket, target.Bucket, StringComparison.Ordinal)
            || !string.Equals(reservation.DestinationBaseUrl, target.BaseUrl, StringComparison.Ordinal)
            || !string.Equals(reservation.CdnDistributionId, target.DistributionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cleanup requires the original public origin and CDN distribution.");
        }
    }
}
