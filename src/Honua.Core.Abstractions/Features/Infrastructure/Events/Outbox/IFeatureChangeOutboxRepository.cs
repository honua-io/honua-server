// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Provider-specific persistence for the feature-change transactional outbox.
/// All multi-node-safe operations (claim, recover, mark) are atomic at the SQL layer.
/// </summary>
/// <remarks>
/// The atomic write path that inserts an outbox row inside the feature-mutation
/// transaction is intentionally not part of this contract: it requires an ambient
/// ADO.NET connection/transaction and is therefore a provider-internal concern
/// (see the provider's feature data-access layer). This abstraction stays
/// transport-agnostic so the outbox dispatcher can consume it without taking a
/// dependency on <see cref="System.Data.Common"/>.
/// </remarks>
public interface IFeatureChangeOutboxRepository
{
    /// <summary>
    /// Atomically claim up to <paramref name="batchSize"/> rows for dispatch.
    /// Implementations must use a single-claim primitive (Postgres: <c>FOR UPDATE SKIP LOCKED</c>;
    /// SQL Server: <c>WITH (UPDLOCK, READPAST)</c>) so concurrent dispatcher nodes never
    /// claim the same row twice.
    /// </summary>
    Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
        string nodeId,
        int batchSize,
        TimeSpan claimTtl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a row as successfully dispatched, but only if <paramref name="ownerNodeId"/>
    /// still holds the active claim. Returns <c>false</c> when the claim has been recovered
    /// or reclaimed by another node so the caller can log a stale-claim warning instead of
    /// overwriting a state owned by a different worker.
    /// </summary>
    Task<bool> MarkDispatchedAsync(Guid outboxId, string ownerNodeId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a dispatch failure if <paramref name="ownerNodeId"/> still holds the active
    /// claim. Increments retry count; transitions to <c>dead_lettered</c> when retry count
    /// reaches <paramref name="maxRetries"/>. Returns <c>false</c> when the claim has been
    /// recovered or reclaimed by another node so the caller can skip retry-bookkeeping for
    /// a row it no longer owns.
    /// </summary>
    Task<bool> MarkFailedAsync(
        Guid outboxId,
        string ownerNodeId,
        string errorMessage,
        int maxRetries,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resets rows whose claim lease has expired back to <c>pending</c> so a healthy
    /// dispatcher can re-claim them. Run periodically by the dispatcher loop.
    /// </summary>
    Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the current backlog snapshot for metrics and health reporting.
    /// </summary>
    Task<OutboxBacklogMetrics> GetBacklogMetricsAsync(CancellationToken cancellationToken);
}
