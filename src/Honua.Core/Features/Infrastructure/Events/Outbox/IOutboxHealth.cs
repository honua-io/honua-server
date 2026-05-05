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
}
