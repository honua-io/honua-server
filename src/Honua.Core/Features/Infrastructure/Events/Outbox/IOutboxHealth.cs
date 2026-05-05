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
    /// UTC timestamp of the most recent successful storage poll (claim, recovery, or
    /// backlog query). Used by the readiness probe to distinguish a freshly-started
    /// dispatcher from one whose claim/backlog queries are repeatedly failing — without
    /// this, a missing table or permission issue would leave <see cref="LastBacklog"/>
    /// null and the probe would report Healthy ("awaiting first pass") indefinitely.
    /// Null when no storage poll has succeeded yet.
    /// </summary>
    DateTimeOffset? LastSuccessfulPollAt { get; }

    /// <summary>
    /// UTC timestamp of the most recent storage poll failure (claim, recovery, or
    /// backlog query). Cleared whenever a storage poll succeeds. The readiness probe
    /// returns Degraded/Unhealthy when this is set after the most recent success so a
    /// stuck dispatcher cannot silently report Healthy. Null when no failures have
    /// occurred since the last successful poll.
    /// </summary>
    DateTimeOffset? LastStorageFailureAt { get; }
}
