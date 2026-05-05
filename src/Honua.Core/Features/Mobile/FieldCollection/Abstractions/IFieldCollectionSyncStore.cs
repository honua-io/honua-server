// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

/// <summary>
/// Persistence boundary for the FieldCollection mobile sync server API (#894).
/// Implementations are expected to provide a stable monotonic generation cursor,
/// idempotent push semantics keyed by mobile-assigned change UUIDs, and
/// deterministic ordered pulls for offline clients.
/// </summary>
public interface IFieldCollectionSyncStore
{
    /// <summary>
    /// Returns the latest generation cursor known to the server. Stable across
    /// readers and suitable for offline clients to persist as their watermark.
    /// </summary>
    Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the last server-acknowledged generation for the given client,
    /// creating a zero-valued entry if none exists.
    /// </summary>
    Task<FieldCollectionSyncCursor> GetSyncCursorAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns ordered FieldCollection changes after <paramref name="sinceGeneration"/>,
    /// up to <paramref name="limit"/> entries. Advances the per-client cursor as a
    /// side effect of every successful pull: to the largest returned generation when
    /// the page is non-empty, or to the committed server watermark when the page is
    /// empty so a caught-up client never re-pulls the same window. The cursor advance
    /// is monotonic — a smaller cursor value can never regress a larger one.
    /// </summary>
    Task<FieldCollectionChangesPage> GetChangesAsync(
        string clientId,
        long sinceGeneration,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a single mobile-pushed change. Repeated calls with the same
    /// <see cref="FieldCollectionPushRequest.ChangeId"/> return the previously
    /// stored outcome without re-applying.
    /// </summary>
    Task<FieldCollectionPushResult> PushChangeAsync(
        FieldCollectionPushRequest request,
        CancellationToken cancellationToken = default);
}
