// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Provider-specific persistence for the feature-change transactional outbox.
/// All multi-node-safe operations (claim, recover, mark) are atomic at the SQL layer.
/// </summary>
public interface IFeatureChangeOutboxRepository
{
    /// <summary>
    /// Inserts an outbox row inside an existing connection + transaction. Use this
    /// overload when the caller already holds the mutation transaction so the outbox
    /// row commits atomically with the feature mutation.
    /// </summary>
    Task WriteOutboxRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts an outbox row using a freshly-opened connection (auto-commit). Used by
    /// callers that have already committed the mutation (single-statement create/update/delete
    /// paths). The window between the mutation commit and this insert is sub-millisecond
    /// in normal operation; restart safety is provided by the dispatcher's recovery loop
    /// for any rows that were committed before a crash.
    /// </summary>
    Task WriteOutboxRowAsync(
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken);

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
    /// Marks a row as successfully dispatched. Idempotent for rows already in the
    /// dispatched state.
    /// </summary>
    Task MarkDispatchedAsync(Guid outboxId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a dispatch failure. Increments retry count; transitions to
    /// <c>dead_lettered</c> when retry count reaches <paramref name="maxRetries"/>.
    /// </summary>
    Task MarkFailedAsync(
        Guid outboxId,
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
