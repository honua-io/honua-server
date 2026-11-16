// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicPublicationCopySource
{
    public required PublicPublicationReservation Reservation { get; init; }
    public required Func<CancellationToken, Task<bool>> IsCurrentAndEligibleAsync { get; init; }
    public required Func<string, CancellationToken, Task<Stream?>> OpenArtifactAsync { get; init; }
}

internal sealed class PublicPublicationCopier(IPublicPublicationJournal journal, IPublicPublicationDestination destination)
{
    public async Task<IReadOnlyDictionary<string, Uri>> CopyAsync(PublicPublicationCopySource source, CancellationToken cancellationToken)
    {
        await ReserveAsync(source, cancellationToken).ConfigureAwait(false);
        await using var session = await journal.OpenAsync(source.Reservation.Key, cancellationToken).ConfigureAwait(false);
        var generation = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null || generation.State != PublicPublicationState.Pending
            || generation.Reservation != source.Reservation)
        {
            throw new InvalidOperationException("Public copying requires its original pending reservation.");
        }
        await RequireCurrentAsync(source, cancellationToken).ConfigureAwait(false);
        var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var key = source.Reservation.Key;
        foreach (var name in new[] { $"{key.PublicationId}.pmtiles", $"{key.PublicationId}.parquet", "descriptor.json", "item.json" })
        {
            urls.Add(name, await CopyArtifactAsync(source, name, cancellationToken).ConfigureAwait(false));
        }
        await RequireCurrentAsync(source, cancellationToken).ConfigureAwait(false);
        await session.MarkCopyCompleteAsync(cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        return urls;
    }

    private async Task ReserveAsync(PublicPublicationCopySource source, CancellationToken cancellationToken)
    {
        await using var session = await journal.OpenAsync(source.Reservation.Key, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(source, cancellationToken).ConfigureAwait(false);
        var previous = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            await session.ReserveAsync(source.Reservation, cancellationToken).ConfigureAwait(false);
        }
        else if (previous.State != PublicPublicationState.Pending || previous.Reservation != source.Reservation)
        {
            throw new InvalidOperationException("Public generation already has a different or terminal reservation.");
        }
        // Persist ownership before any upload; failed/unknown commits cannot emit origin bytes.
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> CopyArtifactAsync(PublicPublicationCopySource source, string fileName, CancellationToken cancellationToken)
    {
        await using var content = await source.OpenArtifactAsync(fileName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Private publication artifact is missing.");
        var reservation = source.Reservation;
        var key = reservation.Key;
        var contentType = fileName.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream";
        var artifact = new PublicPublicationArtifact(new(key.PublicationId, key.Version, fileName), content, contentType)
        {
            SizeBytes = content.CanSeek ? content.Length : null,
            Provenance = new(reservation.ServiceId, reservation.LayerIndex, reservation.MetadataRevision)
        };
        return await destination.PutAsync(artifact, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireCurrentAsync(PublicPublicationCopySource source, CancellationToken cancellationToken)
    {
        if (!await source.IsCurrentAndEligibleAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Public source or access eligibility changed before promotion.");
        }
    }
}
