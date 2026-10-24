// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Durable, immutable query receipts used to continue a bounded materialized query
/// across requests and server restarts. Callers must reauthorize the stored scope.
/// </summary>
public interface IQuerySnapshotStore
{
    /// <summary>Persists an immutable receipt until its absolute expiration.</summary>
    /// <param name="id">Unpredictable server-generated receipt identifier.</param>
    /// <param name="payload">Protocol-owned serialized query and result state.</param>
    /// <param name="expiresAt">Absolute retention deadline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(Guid id, byte[] payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Returns an unexpired receipt, or null for missing or expired identifiers.</summary>
    /// <param name="id">Receipt identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]?> ReadAsync(Guid id, CancellationToken cancellationToken = default);
}
