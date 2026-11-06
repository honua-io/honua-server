// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

// Alert events are append-only; acknowledge, suppress, and resolve live in a companion row so they do not rewrite history.
public enum AlertLifecycleStatus
{
    Open = 0,

    Acknowledged = 1,

    Suppressed = 2,

    Resolved = 3,
}

public sealed record AlertEventLifecycle
{
    public required long EventId { get; init; }

    public required AlertLifecycleStatus Status { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public string? AcknowledgedBy { get; init; }

    public DateTimeOffset? SuppressedUntil { get; init; }

    public string? SuppressedBy { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolvedBy { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AlertEventSummary
{
    public required long EventId { get; init; }

    public required long? RuleId { get; init; }

    public string? RuleName { get; init; }

    public long? ZoneId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required AlertTriggerType TriggerType { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required AlertIncidentStatus IncidentStatus { get; init; }

    public required long IncidentDurationMs { get; init; }

    public required AlertLifecycleStatus LifecycleStatus { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public string? AcknowledgedBy { get; init; }

    public DateTimeOffset? SuppressedUntil { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolvedBy { get; init; }
}

public sealed record AlertEventFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public long? ObjectId { get; init; }

    public long? RuleId { get; init; }

    public IReadOnlyList<AlertSeverity>? Severities { get; init; }

    public IReadOnlyList<AlertIncidentStatus>? IncidentStatuses { get; init; }

    public IReadOnlyList<AlertLifecycleStatus>? LifecycleStatuses { get; init; }

    public int PageSize { get; init; } = 50;

    public string? Cursor { get; init; }
}

public sealed record AlertEventPage
{
    public required IReadOnlyList<AlertEventSummary> Items { get; init; }

    public string? NextCursor { get; init; }
}
