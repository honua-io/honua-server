// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Snapshot of outbox-table backlog state used by metrics and the health check.
/// </summary>
public sealed record OutboxBacklogMetrics
{
    /// <summary>
    /// Rows currently in the dispatch backlog: <c>pending</c> (awaiting first claim),
    /// <c>failed</c> (retriable on the next pass), and <c>claimed</c> (in flight on
    /// some worker, or with an expired lease awaiting recovery). <c>dead_lettered</c>
    /// is reported separately on <see cref="DeadLetteredCount"/> so the readiness
    /// probe can distinguish "still progressing" from "needs operator action".
    /// </summary>
    public required long PendingCount { get; init; }

    /// <summary>Rows that exhausted retries and require operator intervention.</summary>
    public required long DeadLetteredCount { get; init; }

    /// <summary>
    /// Age in seconds of the oldest row in the dispatch backlog (<c>pending</c>,
    /// <c>failed</c>, or <c>claimed</c>), or 0 when the backlog is empty. Matches the
    /// <c>SELECT ... FILTER (WHERE status IN ('pending', 'failed', 'claimed'))</c>
    /// SQL in <c>PostgresFeatureChangeOutboxRepository.GetBacklogMetricsAsync</c>;
    /// claimed rows whose lease has not yet expired still contribute so a stalled
    /// in-flight row surfaces in the gauge alongside truly retriable rows.
    /// </summary>
    public required double OldestPendingAgeSeconds { get; init; }
}
