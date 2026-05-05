// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Reports runtime health of the feature-change transactional outbox. Surfaced on
/// the readiness endpoint so operators can detect dispatch stalls and dead-letter
/// growth before they become silent data loss.
/// </summary>
public interface IOutboxHealth
{
    /// <summary>True when the dispatcher is publishing rows on the configured cadence.</summary>
    bool IsDispatcherRunning { get; }

    /// <summary>UTC timestamp of the most recent successful dispatch, or null when none has occurred yet.</summary>
    DateTimeOffset? LastDispatchAt { get; }

    /// <summary>Most recent backlog snapshot, or null until the dispatcher has executed at least once.</summary>
    OutboxBacklogMetrics? LastBacklog { get; }

    /// <summary>
    /// True when at least one storage poll kind (claim, recovery, or backlog) has a
    /// failure timestamp newer than its most recent success timestamp (or has never
    /// succeeded at all). Each poll kind is tracked independently so a successful
    /// backlog refresh does not mask a still-failing claim, and a successful claim
    /// does not mask a still-failing recovery sweep.
    /// </summary>
    bool IsStoragePollFailing { get; }

    /// <summary>UTC timestamp of the most recent successful <c>ClaimPendingAsync</c>, or null when none has occurred.</summary>
    DateTimeOffset? LastClaimPollSuccessAt { get; }

    /// <summary>UTC timestamp of the most recent <c>ClaimPendingAsync</c> failure, or null when none has occurred.</summary>
    DateTimeOffset? LastClaimPollFailureAt { get; }

    /// <summary>UTC timestamp of the most recent successful <c>RecoverExpiredClaimsAsync</c>, or null when none has occurred.</summary>
    DateTimeOffset? LastRecoveryPollSuccessAt { get; }

    /// <summary>UTC timestamp of the most recent <c>RecoverExpiredClaimsAsync</c> failure, or null when none has occurred.</summary>
    DateTimeOffset? LastRecoveryPollFailureAt { get; }

    /// <summary>UTC timestamp of the most recent successful <c>GetBacklogMetricsAsync</c>, or null when none has occurred.</summary>
    DateTimeOffset? LastBacklogPollSuccessAt { get; }

    /// <summary>UTC timestamp of the most recent <c>GetBacklogMetricsAsync</c> failure, or null when none has occurred.</summary>
    DateTimeOffset? LastBacklogPollFailureAt { get; }

    /// <summary>
    /// Diagnostic aggregate: most recent successful storage poll across all kinds.
    /// The readiness probe uses <see cref="IsStoragePollFailing"/> instead, since
    /// the aggregate can mask a still-failing kind whenever a different kind has
    /// succeeded more recently.
    /// </summary>
    DateTimeOffset? LastSuccessfulPollAt { get; }

    /// <summary>
    /// Diagnostic aggregate: most recent storage poll failure across all kinds.
    /// Both the success and failure aggregates are retained even after a recovery
    /// pass — the dispatcher does not auto-clear either timestamp. Use
    /// <see cref="IsStoragePollFailing"/> to determine pending failure status.
    /// </summary>
    DateTimeOffset? LastStorageFailureAt { get; }
}
