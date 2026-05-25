// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape for an alert event surfaced through the Console Operate API (#1168).
/// </summary>
internal sealed class ObservabilityAlertEventResponse
{
    /// <summary>Event identifier.</summary>
    public required long EventId { get; init; }

    /// <summary>Source rule identifier.</summary>
    public required long RuleId { get; init; }

    /// <summary>Source rule name when available.</summary>
    public string? RuleName { get; init; }

    /// <summary>Optional zone identifier when the rule targets a geofence.</summary>
    public long? ZoneId { get; init; }

    /// <summary>Service identifier the event applies to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Layer identifier the event applies to.</summary>
    public required int LayerId { get; init; }

    /// <summary>Feature identifier the event applies to.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Trigger type (lower-case: enter / exit / dwell / threshold).</summary>
    public required string TriggerType { get; init; }

    /// <summary>Severity (lower-case: info / warning / critical).</summary>
    public required string Severity { get; init; }

    /// <summary>Wall-clock time of the underlying observation.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Incident lifecycle status (started/ongoing/ended).</summary>
    public required string IncidentStatus { get; init; }

    /// <summary>Incident duration in milliseconds.</summary>
    public required long IncidentDurationMs { get; init; }

    /// <summary>Operator lifecycle status (open/acknowledged/suppressed/resolved).</summary>
    public required string LifecycleStatus { get; init; }

    /// <summary>When the event was acknowledged.</summary>
    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>Actor that acknowledged the event.</summary>
    public string? AcknowledgedBy { get; init; }

    /// <summary>Timestamp at which suppression ends.</summary>
    public DateTimeOffset? SuppressedUntil { get; init; }

    /// <summary>When the event was resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Actor that resolved the event.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Source-prefixed resource reference for deep linking (e.g. "alert/42").</summary>
    public required string ResourceRef { get; init; }
}

/// <summary>
/// Page of alert events.
/// </summary>
internal sealed class ObservabilityAlertEventPageResponse
{
    /// <summary>Items returned in this page (newest first).</summary>
    public required IReadOnlyList<ObservabilityAlertEventResponse> Items { get; init; }

    /// <summary>Cursor for the next page or null when none.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// Body for the alert acknowledge action.
/// </summary>
internal sealed class ObservabilityAlertAcknowledgeRequest
{
    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Body for the alert suppress action.
/// </summary>
internal sealed class ObservabilityAlertSuppressRequest
{
    /// <summary>Timestamp at which suppression ends (must be in the future).</summary>
    public required DateTimeOffset SuppressUntil { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Body for the alert resolve action.
/// </summary>
internal sealed class ObservabilityAlertResolveRequest
{
    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }
}
