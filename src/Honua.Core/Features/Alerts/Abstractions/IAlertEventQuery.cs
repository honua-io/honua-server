// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Read-side query surface for alert events plus their operator lifecycle state.
/// </summary>
/// <remarks>
/// Separate from <see cref="IAlertEventStore"/>, which is the append-only writer.
/// Implementations join <c>honua.alert_events</c> with <c>honua.alert_event_lifecycle</c>
/// so callers get a single read model.
/// </remarks>
public interface IAlertEventQuery
{
    /// <summary>
    /// Returns a page of alert event summaries matching the filter,
    /// ordered newest-first by <c>occurred_at</c>.
    /// </summary>
    /// <param name="filter">Query criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single event summary by identifier, or null if not found.
    /// </summary>
    /// <param name="eventId">Event identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read/write surface for the operator lifecycle state attached to an alert event.
/// </summary>
public interface IAlertLifecycleStore
{
    /// <summary>
    /// Returns the lifecycle row for an event, or null when no operator action has been recorded.
    /// </summary>
    /// <param name="eventId">Event identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an acknowledge action against an event.
    /// </summary>
    /// <param name="eventId">Event identifier.</param>
    /// <param name="actor">Stable identifier of the acknowledging operator.</param>
    /// <param name="note">Optional operator note.</param>
    /// <param name="acknowledgedAt">Timestamp of the action (caller supplies for retry-safety).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated lifecycle row, or null when the event does not exist.</returns>
    Task<AlertEventLifecycle?> AcknowledgeAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a suppress action with an expiry timestamp.
    /// </summary>
    /// <param name="eventId">Event identifier.</param>
    /// <param name="actor">Stable identifier of the suppressing operator.</param>
    /// <param name="suppressUntil">Timestamp at which suppression ends. Must be strictly in the future.</param>
    /// <param name="note">Optional operator note.</param>
    /// <param name="suppressedAt">Timestamp of the action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertEventLifecycle?> SuppressAsync(
        long eventId,
        string actor,
        DateTimeOffset suppressUntil,
        string? note,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a resolve action against an event.
    /// </summary>
    /// <param name="eventId">Event identifier.</param>
    /// <param name="actor">Stable identifier of the resolving operator.</param>
    /// <param name="note">Optional operator note.</param>
    /// <param name="resolvedAt">Timestamp of the action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AlertEventLifecycle?> ResolveAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);
}
