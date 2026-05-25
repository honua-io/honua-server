// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Operator lifecycle status applied on top of an immutable alert event.
/// </summary>
/// <remarks>
/// Alert events themselves are append-only. The lifecycle state is tracked in a
/// companion table so acknowledge / suppress / resolve mutations do not rewrite
/// history.
/// </remarks>
public enum AlertLifecycleStatus
{
    /// <summary>
    /// Event has not been acted on. Default state for newly-appended events.
    /// </summary>
    Open = 0,

    /// <summary>
    /// An operator has acknowledged the event but not resolved it.
    /// </summary>
    Acknowledged = 1,

    /// <summary>
    /// Notifications for this event are suppressed until <see cref="AlertEventLifecycle.SuppressedUntil"/>.
    /// </summary>
    Suppressed = 2,

    /// <summary>
    /// Event has been resolved.
    /// </summary>
    Resolved = 3,
}

/// <summary>
/// Mutable operator lifecycle state attached to an immutable alert event.
/// </summary>
public sealed record AlertEventLifecycle
{
    /// <summary>
    /// Identifier of the alert event this lifecycle row tracks.
    /// </summary>
    public required long EventId { get; init; }

    /// <summary>
    /// Current lifecycle status. Defaults to <see cref="AlertLifecycleStatus.Open"/>.
    /// </summary>
    public required AlertLifecycleStatus Status { get; init; }

    /// <summary>
    /// When the event was acknowledged, when applicable.
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>
    /// Actor that acknowledged the event.
    /// </summary>
    public string? AcknowledgedBy { get; init; }

    /// <summary>
    /// When suppression ends, when applicable.
    /// </summary>
    public DateTimeOffset? SuppressedUntil { get; init; }

    /// <summary>
    /// Actor that suppressed the event.
    /// </summary>
    public string? SuppressedBy { get; init; }

    /// <summary>
    /// When the event was resolved.
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// Actor that resolved the event.
    /// </summary>
    public string? ResolvedBy { get; init; }

    /// <summary>
    /// Optional operator note captured at the last transition.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Last-write timestamp.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Flattened summary of an alert event with its current lifecycle state.
/// Optimized for Console list views.
/// </summary>
public sealed record AlertEventSummary
{
    /// <summary>
    /// Event identifier.
    /// </summary>
    public required long EventId { get; init; }

    /// <summary>
    /// Source rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Source rule name when available.
    /// </summary>
    public string? RuleName { get; init; }

    /// <summary>
    /// Optional zone identifier when the rule targets a geofence.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Service identifier the event applies to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer identifier the event applies to.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Feature identifier the event applies to.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Trigger type that produced this event.
    /// </summary>
    public required AlertTriggerType TriggerType { get; init; }

    /// <summary>
    /// Severity assigned by the originating rule.
    /// </summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>
    /// When the underlying observation was made.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Incident lifecycle status (Started/Ongoing/Ended) carried on the event.
    /// </summary>
    public required AlertIncidentStatus IncidentStatus { get; init; }

    /// <summary>
    /// Incident duration in milliseconds carried on the event.
    /// </summary>
    public required long IncidentDurationMs { get; init; }

    /// <summary>
    /// Operator lifecycle status for the event.
    /// </summary>
    public required AlertLifecycleStatus LifecycleStatus { get; init; }

    /// <summary>
    /// When the event was acknowledged, when applicable.
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>
    /// Actor that acknowledged the event.
    /// </summary>
    public string? AcknowledgedBy { get; init; }

    /// <summary>
    /// When suppression ends, when applicable.
    /// </summary>
    public DateTimeOffset? SuppressedUntil { get; init; }

    /// <summary>
    /// When the event was resolved.
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// Actor that resolved the event.
    /// </summary>
    public string? ResolvedBy { get; init; }
}

/// <summary>
/// Filter criteria for <c>IAlertEventQuery.ListAsync</c>.
/// </summary>
public sealed record AlertEventFilter
{
    /// <summary>
    /// Lower bound (inclusive) for <c>occurred_at</c>.
    /// </summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// Upper bound (exclusive) for <c>occurred_at</c>.
    /// </summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// Optional service identifier filter.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Optional layer identifier filter.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Optional feature identifier filter.
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>
    /// Optional rule identifier filter.
    /// </summary>
    public long? RuleId { get; init; }

    /// <summary>
    /// Optional severity filter — events at any of these severities are returned.
    /// </summary>
    public IReadOnlyList<AlertSeverity>? Severities { get; init; }

    /// <summary>
    /// Optional incident status filter.
    /// </summary>
    public IReadOnlyList<AlertIncidentStatus>? IncidentStatuses { get; init; }

    /// <summary>
    /// Optional operator lifecycle status filter.
    /// </summary>
    public IReadOnlyList<AlertLifecycleStatus>? LifecycleStatuses { get; init; }

    /// <summary>
    /// Maximum number of rows to return. Implementations clamp to a server-side maximum.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Opaque pagination cursor returned by a prior page.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// A single page of alert event summaries with optional continuation cursor.
/// </summary>
public sealed record AlertEventPage
{
    /// <summary>
    /// Items returned in this page, ordered newest-first.
    /// </summary>
    public required IReadOnlyList<AlertEventSummary> Items { get; init; }

    /// <summary>
    /// Cursor to retrieve the next page, or null when there are no more rows.
    /// </summary>
    public string? NextCursor { get; init; }
}
