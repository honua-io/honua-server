// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Snapshot of outbox-table backlog state used by metrics and the health check.
/// </summary>
public sealed record OutboxBacklogMetrics
{
    /// <summary>Rows currently waiting to be claimed (status = pending or failed).</summary>
    public required long PendingCount { get; init; }

    /// <summary>Rows that exhausted retries and require operator intervention.</summary>
    public required long DeadLetteredCount { get; init; }

    /// <summary>Age of the oldest claimable row in seconds, or 0 when the backlog is empty.</summary>
    public required double OldestPendingAgeSeconds { get; init; }
}
