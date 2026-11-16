// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

public sealed record PublicPublicationKey(string PublicationId, string Version);

public sealed record PublicPublicationReservation
{
    public required PublicPublicationKey Key { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerIndex { get; init; }
    public required long MetadataRevision { get; init; }
    public required string DestinationBucket { get; init; }
    public required string DestinationBaseUrl { get; init; }
    public required string CdnDistributionId { get; init; }
}

public sealed record PublicPublicationGeneration
{
    public required PublicPublicationReservation Reservation { get; init; }
    public required PublicPublicationState State { get; init; }
    public required IReadOnlyList<string> PurgeIds { get; init; }
}

public enum PublicPublicationState
{
    Pending,
    Ready,
    Revoking,
    Revoked
}

public sealed record PublicPublicationPurge(string DistributionId, IReadOnlyList<string> InvalidationIds);

public interface IPublicPublicationJournal
{
    Task<IPublicPublicationSession> OpenAsync(PublicPublicationKey key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicPublicationGeneration>> ReadOutstandingAsync(CancellationToken cancellationToken = default);
}

// Holds the shared access-write fence until commit or disposal. Reserve and commit before
// uploading; reopen, verify Pending, then keep the session through copy/promotion.
// Revocation uses the same fence so an old worker cannot upload after deletion.
public interface IPublicPublicationSession : IAsyncDisposable
{
    Task<PublicPublicationGeneration?> ReadAsync(CancellationToken cancellationToken = default);
    Task ReserveAsync(PublicPublicationReservation reservation, CancellationToken cancellationToken = default);
    Task MarkCopyCompleteAsync(CancellationToken cancellationToken = default);
    Task BeginRevocationAsync(CancellationToken cancellationToken = default);
    Task RecordOriginDeletedAsync(CancellationToken cancellationToken = default);
    Task RecordPurgeSubmittedAsync(PublicPublicationPurge purge, CancellationToken cancellationToken = default);
    Task RecordRevocationCompletedAsync(PublicPublicationPurge completedPurge, CancellationToken cancellationToken = default);
    Task CommitAsync(CancellationToken cancellationToken = default);
}
