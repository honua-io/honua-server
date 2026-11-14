// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

public sealed record CatalogActivationWatch
{
    public required long Id { get; init; }
    public required long Revision { get; init; }
    public required string ExpectedEtag { get; init; }
    public required DateTimeOffset ActivatedAt { get; init; }
    public long? RollbackRevision { get; init; }
    public string? ChangeId { get; init; }
    public string? Reason { get; init; }
}

public sealed record CatalogRollbackRequest(long ActivationId, string Reason);

public interface ICatalogActivationWatchStore
{
    Task<IReadOnlyList<CatalogActivationWatch>> ReadPendingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogActivationWatch>> ReadRecentRollbacksAsync(CancellationToken cancellationToken = default);

    Task<bool> TryRollbackAsync(CatalogRollbackRequest request, CancellationToken cancellationToken = default);

    Task ExpireAsync(long activationId, CancellationToken cancellationToken = default);

    Task CompletePurgeAsync(long activationId, CancellationToken cancellationToken = default);
}
