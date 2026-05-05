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
    /// up to <paramref name="limit"/> entries. Pure read — never advances the per-client
    /// cursor. Mobile clients drive the cursor explicitly via
    /// <see cref="RecordSyncCursorAsync"/> after local persistence succeeds; treating the
    /// HTTP response as an implicit acknowledgement would lose unapplied changes if the
    /// caller crashed or failed local persistence between the response and the apply.
    /// <see cref="FieldCollectionChangesPage.NextCursor"/> is a recommended next watermark
    /// (the largest returned generation when the page is non-empty, otherwise the
    /// committed server watermark clamped to that watermark) — it does not reflect any
    /// stored state.
    /// </summary>
    Task<FieldCollectionChangesPage> GetChangesAsync(
        string clientId,
        long sinceGeneration,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the client's last-applied generation after local persistence succeeds.
    /// Monotonic — a smaller value can never regress a larger one. Implementations must
    /// reject values that exceed the committed server watermark so that a stale or
    /// poisoned client cursor cannot persist a future generation.
    /// </summary>
    Task RecordSyncCursorAsync(
        string clientId,
        long lastSyncGeneration,
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
