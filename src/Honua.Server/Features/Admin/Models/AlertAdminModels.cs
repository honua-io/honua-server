// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Zone create/update request payload.
/// </summary>
internal sealed class AlertZoneRequest
{
    /// <summary>
    /// Service identifier owning this zone.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Human-readable zone name.
    /// </summary>
    public required string ZoneName { get; init; }

    /// <summary>
    /// Zone geometry in WKT format.
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// SRID associated with the provided geometry.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Optional metadata key-values.
    /// </summary>
    public Dictionary<string, string?>? Metadata { get; init; }

    /// <summary>
    /// Indicates whether the zone is active.
    /// </summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Zone response payload.
/// </summary>
internal sealed class AlertZoneResponse
{
    /// <summary>
    /// Zone identifier.
    /// </summary>
    public required long ZoneId { get; init; }

    /// <summary>
    /// Service identifier owning this zone.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Zone name.
    /// </summary>
    public required string ZoneName { get; init; }

    /// <summary>
    /// Geometry in WKT format.
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// Geometry SRID.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Zone metadata key-values.
    /// </summary>
    public required Dictionary<string, string?> Metadata { get; init; }

    /// <summary>
    /// Indicates whether this zone is active.
    /// </summary>
    public required bool IsActive { get; init; }
}

/// <summary>
/// Rule create/update request payload.
/// </summary>
internal sealed class AlertRuleRequest
{
    /// <summary>
    /// Service identifier targeted by this rule.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer identifier targeted by this rule.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Optional zone identifier.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Rule name.
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Trigger type (enter|exit|dwell|threshold).
    /// </summary>
    public required string TriggerType { get; init; }

    /// <summary>
    /// Conditions payload serialized as JSON.
    /// </summary>
    public string ConditionsJson { get; init; } = "{}";

    /// <summary>
    /// Cooldown in seconds.
    /// </summary>
    public int CooldownSeconds { get; init; }

    /// <summary>
    /// Severity (info|warning|critical).
    /// </summary>
    public string Severity { get; init; } = "warning";

    /// <summary>
    /// Required edition (pro|enterprise).
    /// </summary>
    public string EditionRequired { get; init; } = "pro";

    /// <summary>
    /// Delivery channels (webhook|websocket|email|digest|aws_sns|azure_eventgrid|slack|microsoft_teams|aws_sqs|azure_eventhub).
    /// </summary>
    public string[]? Channels { get; init; }

    /// <summary>
    /// Indicates whether this rule is active.
    /// </summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Rule response payload.
/// </summary>
internal sealed class AlertRuleResponse
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Service identifier.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Optional zone identifier.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Rule name.
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Trigger type.
    /// </summary>
    public required string TriggerType { get; init; }

    /// <summary>
    /// Rule conditions as JSON.
    /// </summary>
    public required string ConditionsJson { get; init; }

    /// <summary>
    /// Cooldown in seconds.
    /// </summary>
    public required int CooldownSeconds { get; init; }

    /// <summary>
    /// Severity.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Required edition.
    /// </summary>
    public required string EditionRequired { get; init; }

    /// <summary>
    /// Delivery channels.
    /// </summary>
    public required string[] Channels { get; init; }

    /// <summary>
    /// Indicates whether the rule is active.
    /// </summary>
    public required bool IsActive { get; init; }
}

/// <summary>
/// Request to set the enabled state for an alert rule.
/// </summary>
internal sealed class AlertRuleEnabledRequest
{
    /// <summary>
    /// New enabled state.
    /// </summary>
    public required bool Enabled { get; init; }
}

/// <summary>
/// Draft rule validation request.
/// </summary>
internal sealed class AlertRuleTestRequest
{
    /// <summary>
    /// Rule draft to validate.
    /// </summary>
    public required AlertRuleRequest Rule { get; init; }

    /// <summary>
    /// Optional draft zone to validate with the rule before it is persisted.
    /// </summary>
    public AlertZoneRequest? Zone { get; init; }
}

/// <summary>
/// Rule validation response.
/// </summary>
internal sealed class AlertRuleTestResponse
{
    /// <summary>
    /// Indicates whether the draft can be persisted safely.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Validation errors that must be fixed before persistence.
    /// </summary>
    public required string[] Errors { get; init; }

    /// <summary>
    /// Non-blocking warnings for the operator.
    /// </summary>
    public required string[] Warnings { get; init; }

    /// <summary>
    /// Per-channel delivery validation results.
    /// </summary>
    public required AlertChannelValidationResponse[] DeliveryChannels { get; init; }

    /// <summary>
    /// Timestamp at which validation was evaluated.
    /// </summary>
    public required DateTimeOffset EvaluatedAt { get; init; }
}

/// <summary>
/// Delivery channel validation result.
/// </summary>
internal sealed class AlertChannelValidationResponse
{
    /// <summary>
    /// Channel name.
    /// </summary>
    public required string Channel { get; init; }

    /// <summary>
    /// Channel state: configured, unconfigured, disabled, unauthorized, rate_limited, or failing.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Indicates whether the current edition allows the channel.
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Indicates whether server configuration can deliver the channel.
    /// </summary>
    public required bool IsConfigured { get; init; }

    /// <summary>
    /// Human-readable validation message.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Operational health response for a rule.
/// </summary>
internal sealed class AlertRuleHealthResponse
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required long RuleId { get; init; }

    /// <summary>
    /// Most recent evaluator state update time.
    /// </summary>
    public DateTimeOffset? LastEvaluatedAt { get; init; }

    /// <summary>
    /// Most recent trigger time.
    /// </summary>
    public DateTimeOffset? LastTriggeredAt { get; init; }

    /// <summary>
    /// Count of open or acknowledged started/ongoing incidents.
    /// </summary>
    public required int ActiveIncidentCount { get; init; }

    /// <summary>
    /// Count of trigger events in the recent health window.
    /// </summary>
    public required int RecentTriggerCount { get; init; }

    /// <summary>
    /// Count of feature states currently inside cooldown.
    /// </summary>
    public required int CoolingDownFeatureCount { get; init; }

    /// <summary>
    /// Latest cooldown expiry across currently cooling down feature states.
    /// </summary>
    public DateTimeOffset? NextCooldownExpiresAt { get; init; }

    /// <summary>
    /// Retryable delivery failure count.
    /// </summary>
    public required int DeliveryFailureCount { get; init; }

    /// <summary>
    /// Dead-letter delivery count.
    /// </summary>
    public required int DeadLetterCount { get; init; }

    /// <summary>
    /// Recent linked event identifiers, newest first.
    /// </summary>
    public required long[] LinkedEventIds { get; init; }

    /// <summary>
    /// Per-channel delivery health.
    /// </summary>
    public required AlertRuleDeliveryHealthResponse[] DeliveryChannels { get; init; }

    /// <summary>
    /// Recent trigger summaries.
    /// </summary>
    public required AlertRuleRecentTriggerResponse[] RecentTriggers { get; init; }
}

/// <summary>
/// Per-channel rule delivery health response.
/// </summary>
internal sealed class AlertRuleDeliveryHealthResponse
{
    /// <summary>
    /// Channel name.
    /// </summary>
    public required string Channel { get; init; }

    /// <summary>
    /// Channel state: configured, disabled, rate_limited, or failing.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Pending dispatch row count.
    /// </summary>
    public required int PendingCount { get; init; }

    /// <summary>
    /// Processing dispatch row count.
    /// </summary>
    public required int ProcessingCount { get; init; }

    /// <summary>
    /// Delivered dispatch row count.
    /// </summary>
    public required int DeliveredCount { get; init; }

    /// <summary>
    /// Retryable failed dispatch row count.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>
    /// Dead-lettered dispatch row count.
    /// </summary>
    public required int DeadLetterCount { get; init; }

    /// <summary>
    /// Most recent delivery attempt time.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>
    /// Most recent successful delivery time.
    /// </summary>
    public DateTimeOffset? LastDeliveredAt { get; init; }

    /// <summary>
    /// Last sanitized delivery error.
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Recent trigger response for rule health.
/// </summary>
internal sealed class AlertRuleRecentTriggerResponse
{
    /// <summary>
    /// Event identifier.
    /// </summary>
    public required long EventId { get; init; }

    /// <summary>
    /// Trigger type.
    /// </summary>
    public required string TriggerType { get; init; }

    /// <summary>
    /// Severity.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Observation timestamp.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Incident lifecycle status.
    /// </summary>
    public required string IncidentStatus { get; init; }

    /// <summary>
    /// Operator lifecycle status.
    /// </summary>
    public required string LifecycleStatus { get; init; }

    /// <summary>
    /// Operate event resource reference.
    /// </summary>
    public required string ResourceRef { get; init; }
}

/// <summary>
/// Audit details emitted for alert admin changes.
/// </summary>
internal sealed class AlertAdminAuditDetails
{
    /// <summary>
    /// Service identifier.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Layer identifier.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Rule identifier.
    /// </summary>
    public long? RuleId { get; init; }

    /// <summary>
    /// Zone identifier.
    /// </summary>
    public long? ZoneId { get; init; }

    /// <summary>
    /// Enabled state when the action changes it.
    /// </summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// Source-generated JSON metadata for alert admin payloads.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AlertZoneRequest))]
[JsonSerializable(typeof(AlertZoneResponse))]
[JsonSerializable(typeof(AlertZoneResponse[]))]
[JsonSerializable(typeof(AlertRuleRequest))]
[JsonSerializable(typeof(AlertRuleEnabledRequest))]
[JsonSerializable(typeof(AlertRuleResponse))]
[JsonSerializable(typeof(AlertRuleResponse[]))]
[JsonSerializable(typeof(AlertRuleTestRequest))]
[JsonSerializable(typeof(AlertRuleTestResponse))]
[JsonSerializable(typeof(AlertChannelValidationResponse))]
[JsonSerializable(typeof(AlertChannelValidationResponse[]))]
[JsonSerializable(typeof(AlertRuleHealthResponse))]
[JsonSerializable(typeof(AlertRuleDeliveryHealthResponse))]
[JsonSerializable(typeof(AlertRuleDeliveryHealthResponse[]))]
[JsonSerializable(typeof(AlertRuleRecentTriggerResponse))]
[JsonSerializable(typeof(AlertRuleRecentTriggerResponse[]))]
[JsonSerializable(typeof(AlertAdminAuditDetails))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse[]>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse[]>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleTestResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleHealthResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class AlertAdminJsonContext : JsonSerializerContext
{
}
