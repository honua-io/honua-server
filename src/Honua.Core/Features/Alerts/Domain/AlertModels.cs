// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Edition level required for alert capabilities.
/// </summary>
public enum AlertEdition
{
    /// <summary>
    /// Pro edition features.
    /// </summary>
    Pro = 1,

    /// <summary>
    /// Enterprise edition features.
    /// </summary>
    Enterprise = 2,
}

/// <summary>
/// Trigger types supported by the alert evaluator.
/// </summary>
public enum AlertTriggerType
{
    /// <summary>
    /// Fires when a feature transitions from outside to inside a zone.
    /// </summary>
    Enter = 1,

    /// <summary>
    /// Fires when a feature transitions from inside to outside a zone.
    /// </summary>
    Exit = 2,

    /// <summary>
    /// Fires when a feature remains inside a zone for a configured dwell duration.
    /// </summary>
    Dwell = 3,

    /// <summary>
    /// Fires when a configured metric crosses a threshold.
    /// </summary>
    Threshold = 4,
}

/// <summary>
/// Severity level assigned to generated alert events.
/// </summary>
public enum AlertSeverity
{
    /// <summary>
    /// Informational event.
    /// </summary>
    Info = 1,

    /// <summary>
    /// Warning-level event.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Critical event.
    /// </summary>
    Critical = 3,
}

/// <summary>
/// Delivery channel used for dispatching alert events.
/// </summary>
public enum AlertChannelType
{
    /// <summary>
    /// HTTP webhook delivery.
    /// </summary>
    Webhook = 1,

    /// <summary>
    /// WebSocket push delivery.
    /// </summary>
    WebSocket = 2,

    /// <summary>
    /// Email delivery.
    /// </summary>
    Email = 3,

    /// <summary>
    /// Batched digest delivery.
    /// </summary>
    Digest = 4,

    /// <summary>
    /// AWS Simple Notification Service delivery.
    /// </summary>
    AwsSns = 5,

    /// <summary>
    /// Azure Event Grid delivery.
    /// </summary>
    AzureEventGrid = 6,

    /// <summary>
    /// Slack incoming webhook delivery.
    /// </summary>
    Slack = 7,

    /// <summary>
    /// Microsoft Teams incoming webhook delivery.
    /// </summary>
    MicrosoftTeams = 8,

    /// <summary>
    /// AWS Simple Queue Service delivery.
    /// </summary>
    AwsSqs = 9,

    /// <summary>
    /// Azure Event Hubs delivery.
    /// </summary>
    AzureEventHub = 10,
}

/// <summary>
/// Incident lifecycle status for alert events, following the Started/Ongoing/Ended model.
/// </summary>
public enum AlertIncidentStatus
{
    /// <summary>
    /// Initial observation that triggered the alert condition.
    /// </summary>
    Started = 1,

    /// <summary>
    /// Subsequent observations that continue to satisfy the alert condition.
    /// </summary>
    Ongoing = 2,

    /// <summary>
    /// Observation that no longer satisfies the alert condition (incident resolved).
    /// </summary>
    Ended = 3,
}

/// <summary>
/// Outbox status for dispatch lifecycle.
/// </summary>
public enum AlertDispatchStatus
{
    /// <summary>
    /// Awaiting dispatch.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Currently being processed by a dispatcher.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Successfully delivered.
    /// </summary>
    Delivered = 2,

    /// <summary>
    /// Delivery failed and can be retried.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Delivery exhausted retries and was moved to dead letter.
    /// </summary>
    DeadLetter = 4,
}

/// <summary>
/// Feature change operation produced by durable change tracking.
/// </summary>
public enum AlertChangeOperation
{
    /// <summary>
    /// Feature was inserted.
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Feature was updated.
    /// </summary>
    Update = 2,

    /// <summary>
    /// Feature was deleted.
    /// </summary>
    Delete = 3,
}

/// <summary>
/// Geofence zone definition used during spatial rule evaluation.
/// </summary>
public sealed record AlertZoneDefinition
{
    /// <summary>
    /// Persistent zone identifier.
    /// </summary>
    public required long ZoneId { get; init; }

    /// <summary>
    /// Service identifier owning this zone.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Human-readable zone name.
    /// </summary>
    public required string ZoneName { get; init; }

    /// <summary>
    /// Zone geometry in Well-Known Binary format.
    /// </summary>
    public byte[]? Geometry { get; init; }

    /// <summary>
    /// Spatial reference identifier of the stored zone geometry.
    /// </summary>
    public int? GeometrySrid { get; init; }

    /// <summary>
    /// Indicates whether the zone is active.
    /// </summary>
    public required bool IsActive { get; init; }

    /// <summary>
    /// Optional metadata for UI and operations.
    /// </summary>
    public ImmutableDictionary<string, string?> Metadata { get; init; } = ImmutableDictionary<string, string?>.Empty;
}

/// <summary>
/// Rule definition that maps triggers to layers, filters, and channels.
/// </summary>
public sealed record AlertRuleDefinition
{
    /// <summary>
    /// Persistent rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Service identifier targeted by this rule.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer identifier targeted by this rule.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Optional geofence zone identifier. Null for non-zone threshold rules.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Rule name for administration and diagnostics.
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Trigger type evaluated by this rule.
    /// </summary>
    public required AlertTriggerType TriggerType { get; init; }

    /// <summary>
    /// Rule condition payload stored as JSON.
    /// </summary>
    public string ConditionsJson { get; init; } = "{}";

    /// <summary>
    /// Cooldown period in seconds between alerts for the same feature.
    /// </summary>
    public int CooldownSeconds { get; init; }

    /// <summary>
    /// Severity emitted for resulting events.
    /// </summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>
    /// Edition level required to execute this rule.
    /// </summary>
    public required AlertEdition EditionRequired { get; init; }

    /// <summary>
    /// Delivery channels for generated events.
    /// </summary>
    public ImmutableArray<AlertChannelType> Channels { get; init; } = ImmutableArray<AlertChannelType>.Empty;

    /// <summary>
    /// Indicates whether the rule is enabled.
    /// </summary>
    public required bool IsActive { get; init; }
}

/// <summary>
/// Delivery health summary for a single channel on an alert rule.
/// </summary>
public sealed record AlertRuleDeliveryHealth
{
    /// <summary>
    /// Delivery channel represented by this summary.
    /// </summary>
    public required AlertChannelType ChannelType { get; init; }

    /// <summary>
    /// Number of pending dispatch outbox rows for this channel.
    /// </summary>
    public required int PendingCount { get; init; }

    /// <summary>
    /// Number of currently processing dispatch outbox rows for this channel.
    /// </summary>
    public required int ProcessingCount { get; init; }

    /// <summary>
    /// Number of successfully delivered dispatch rows for this channel.
    /// </summary>
    public required int DeliveredCount { get; init; }

    /// <summary>
    /// Number of retryable failed dispatch rows for this channel.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>
    /// Number of dead-lettered dispatch rows for this channel.
    /// </summary>
    public required int DeadLetterCount { get; init; }

    /// <summary>
    /// Most recent delivery attempt time for this channel, when known.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>
    /// Most recent successful delivery time for this channel, when known.
    /// </summary>
    public DateTimeOffset? LastDeliveredAt { get; init; }

    /// <summary>
    /// Most recent sanitized delivery error for this channel, when present.
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Operational health summary for an alert rule.
/// </summary>
public sealed record AlertRuleHealthSnapshot
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Most recent evaluator state update for this rule, when available.
    /// </summary>
    public DateTimeOffset? LastEvaluatedAt { get; init; }

    /// <summary>
    /// Most recent alert event emitted for this rule, when available.
    /// </summary>
    public DateTimeOffset? LastTriggeredAt { get; init; }

    /// <summary>
    /// Count of current evaluator state rows that represent active incidents for this rule.
    /// </summary>
    public required int ActiveIncidentCount { get; init; }

    /// <summary>
    /// Count of recent trigger events in the health window.
    /// </summary>
    public required int RecentTriggerCount { get; init; }

    /// <summary>
    /// Count of evaluator state rows currently inside their cooldown window.
    /// </summary>
    public required int CoolingDownFeatureCount { get; init; }

    /// <summary>
    /// Latest cooldown expiry across currently cooling down features.
    /// </summary>
    public DateTimeOffset? NextCooldownExpiresAt { get; init; }

    /// <summary>
    /// Count of retryable delivery failures for this rule.
    /// </summary>
    public required int DeliveryFailureCount { get; init; }

    /// <summary>
    /// Count of dead-lettered delivery rows for this rule.
    /// </summary>
    public required int DeadLetterCount { get; init; }

    /// <summary>
    /// Recent event identifiers linked to this rule, newest first.
    /// </summary>
    public ImmutableArray<long> LinkedEventIds { get; init; } = ImmutableArray<long>.Empty;

    /// <summary>
    /// Per-channel delivery health summaries.
    /// </summary>
    public ImmutableArray<AlertRuleDeliveryHealth> DeliveryChannels { get; init; } = ImmutableArray<AlertRuleDeliveryHealth>.Empty;
}

/// <summary>
/// Durable feature change input consumed by alert evaluation.
/// </summary>
public readonly record struct AlertChange
{
    /// <summary>
    /// Monotonic generation value from durable change tracking.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Layer identifier of the changed feature.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Feature object identifier.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Operation applied to the feature.
    /// </summary>
    public required AlertChangeOperation Operation { get; init; }

    /// <summary>
    /// Timestamp when the change was recorded.
    /// </summary>
    public required DateTimeOffset ChangedAt { get; init; }
}

/// <summary>
/// Lookup key for batched active-rule loading.
/// </summary>
public readonly record struct AlertRuleLookupKey(string? ServiceId, int LayerId);

/// <summary>
/// Lookup key for batched state loading.
/// </summary>
public readonly record struct AlertStateLookupKey(long RuleId, int LayerId, long ObjectId);

/// <summary>
/// Persisted evaluator state for a rule-feature pair.
/// </summary>
public sealed record AlertStateSnapshot
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Feature object identifier.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Indicates whether the feature is currently inside the zone.
    /// </summary>
    public required bool Inside { get; init; }

    /// <summary>
    /// Timestamp when the feature most recently entered the zone.
    /// </summary>
    public DateTimeOffset? EnteredAt { get; init; }

    /// <summary>
    /// Timestamp of the most recent alert for cooldown enforcement.
    /// </summary>
    public DateTimeOffset? LastAlertAt { get; init; }

    /// <summary>
    /// Last generation number evaluated for this state row.
    /// </summary>
    public required long LastGeneration { get; init; }

    /// <summary>
    /// Serialized threshold state used for edge detection.
    /// </summary>
    public string ThresholdStateJson { get; init; } = "{}";
}

/// <summary>
/// Immutable alert event produced by transition evaluation.
/// </summary>
public sealed record AlertEventEnvelope
{
    /// <summary>
    /// Idempotency key used to deduplicate inserts.
    /// </summary>
    public required string DedupeKey { get; init; }

    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Optional zone identifier associated with the event.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Service identifier.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Feature object identifier.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Trigger type that produced this event.
    /// </summary>
    public required AlertTriggerType TriggerType { get; init; }

    /// <summary>
    /// Generation associated with this event.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>
    /// Event severity.
    /// </summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>
    /// Event timestamp.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// JSON payload used by downstream delivery sinks.
    /// </summary>
    public string PayloadJson { get; init; } = "{}";

    /// <summary>
    /// Incident lifecycle status (Started, Ongoing, Ended).
    /// </summary>
    public AlertIncidentStatus IncidentStatus { get; init; } = AlertIncidentStatus.Started;

    /// <summary>
    /// Duration of the incident in milliseconds from when it started.
    /// </summary>
    public long IncidentDurationMs { get; init; }
}

/// <summary>
/// Dispatch work item in the outbox.
/// </summary>
public sealed record AlertDispatchItem
{
    /// <summary>
    /// Dispatch identifier.
    /// </summary>
    public required long DispatchId { get; init; }

    /// <summary>
    /// Associated event identifier.
    /// </summary>
    public required long EventId { get; init; }

    /// <summary>
    /// Delivery channel.
    /// </summary>
    public required AlertChannelType ChannelType { get; init; }

    /// <summary>
    /// Optional destination override for the channel.
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>
    /// Current outbox status.
    /// </summary>
    public required AlertDispatchStatus Status { get; init; }

    /// <summary>
    /// Number of attempts already performed.
    /// </summary>
    public required int Attempts { get; init; }

    /// <summary>
    /// Maximum attempts permitted.
    /// </summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Next eligible attempt time.
    /// </summary>
    public required DateTimeOffset NextAttemptAt { get; init; }
}

/// <summary>
/// Persisted worker cursor metadata.
/// </summary>
public sealed record AlertWorkerCheckpoint
{
    /// <summary>
    /// Worker identifier.
    /// </summary>
    public required string WorkerName { get; init; }

    /// <summary>
    /// Most recent processed generation.
    /// </summary>
    public required long LastGeneration { get; init; }

    /// <summary>
    /// Most recent dwell sweep timestamp.
    /// </summary>
    public DateTimeOffset? LastDwellSweepAt { get; init; }
}

/// <summary>
/// Result of evaluating one feature change against one alert rule.
/// </summary>
public sealed record AlertEvaluationResult
{
    /// <summary>
    /// Updated state snapshot to persist for the evaluated feature/rule pair.
    /// </summary>
    public AlertStateSnapshot? UpdatedState { get; init; }

    /// <summary>
    /// Events generated during the transition evaluation.
    /// </summary>
    public ImmutableArray<AlertEventEnvelope> Events { get; init; } = ImmutableArray<AlertEventEnvelope>.Empty;
}
