// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

internal sealed class AlertZoneRequest
{
    public required string ServiceId { get; init; }

    public required string ZoneName { get; init; }

    public string? Wkt { get; init; }

    public int? Srid { get; init; }

    public Dictionary<string, string?>? Metadata { get; init; }

    public bool IsActive { get; init; } = true;
}

internal sealed class AlertZoneResponse
{
    public required long ZoneId { get; init; }

    public required string ServiceId { get; init; }

    public required string ZoneName { get; init; }

    public string? Wkt { get; init; }

    public int? Srid { get; init; }

    public required Dictionary<string, string?> Metadata { get; init; }

    public required bool IsActive { get; init; }
}

internal sealed class AlertRuleRequest
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public long? ZoneId { get; init; }

    public required string RuleName { get; init; }

    public required string TriggerType { get; init; }

    public string ConditionsJson { get; init; } = "{}";

    public int CooldownSeconds { get; init; }

    public string Severity { get; init; } = "warning";

    public string EditionRequired { get; init; } = "pro";

    public string[]? Channels { get; init; }

    public bool IsActive { get; init; } = true;
}

internal sealed class AlertRuleResponse
{
    public required long RuleId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public long? ZoneId { get; init; }

    public required string RuleName { get; init; }

    public required string TriggerType { get; init; }

    public required string ConditionsJson { get; init; }

    public required int CooldownSeconds { get; init; }

    public required string Severity { get; init; }

    public required string EditionRequired { get; init; }

    public required string[] Channels { get; init; }

    public required bool IsActive { get; init; }
}

internal sealed class AlertRuleEnabledRequest
{
    public required bool Enabled { get; init; }
}

internal sealed class AlertRuleTestRequest
{
    public required AlertRuleRequest Rule { get; init; }

    public AlertZoneRequest? Zone { get; init; }
}

internal sealed class AlertRuleTestResponse
{
    public required bool IsValid { get; init; }

    public required string[] Errors { get; init; }

    public required string[] Warnings { get; init; }

    public required AlertChannelValidationResponse[] DeliveryChannels { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }
}

internal sealed class AlertChannelValidationResponse
{
    public required string Channel { get; init; }

    public required string Status { get; init; }

    public required bool IsAllowed { get; init; }

    public required bool IsConfigured { get; init; }

    public required string Message { get; init; }
}

internal sealed class AlertRuleHealthResponse
{
    public required long RuleId { get; init; }

    public DateTimeOffset? LastEvaluatedAt { get; init; }

    public DateTimeOffset? LastTriggeredAt { get; init; }

    public required int ActiveIncidentCount { get; init; }

    public required int RecentTriggerCount { get; init; }

    public required int CoolingDownFeatureCount { get; init; }

    public DateTimeOffset? NextCooldownExpiresAt { get; init; }

    public required int DeliveryFailureCount { get; init; }

    public required int DeadLetterCount { get; init; }

    public required long[] LinkedEventIds { get; init; }

    public required AlertRuleDeliveryHealthResponse[] DeliveryChannels { get; init; }

    public required AlertRuleRecentTriggerResponse[] RecentTriggers { get; init; }
}

internal sealed class AlertRuleDeliveryHealthResponse
{
    public required string Channel { get; init; }

    public required string Status { get; init; }

    public required int PendingCount { get; init; }

    public required int ProcessingCount { get; init; }

    public required int DeliveredCount { get; init; }

    public required int FailedCount { get; init; }

    public required int DeadLetterCount { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? LastDeliveredAt { get; init; }

    public string? LastError { get; init; }
}

internal sealed class AlertRuleRecentTriggerResponse
{
    public required long EventId { get; init; }

    public required string TriggerType { get; init; }

    public required string Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string IncidentStatus { get; init; }

    public required string LifecycleStatus { get; init; }

    public required string ResourceRef { get; init; }
}

internal sealed class AlertRuleEventPageResponse
{
    public required long RuleId { get; init; }

    public required AlertRuleRecentTriggerResponse[] Items { get; init; }

    public string? NextCursor { get; init; }
}

internal sealed class AlertAdminAuditDetails
{
    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public long? RuleId { get; init; }

    public long? ZoneId { get; init; }

    public bool? Enabled { get; init; }
}

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
[JsonSerializable(typeof(AlertRuleEventPageResponse))]
[JsonSerializable(typeof(AlertAdminAuditDetails))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse[]>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse[]>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleTestResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleHealthResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleEventPageResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class AlertAdminJsonContext : JsonSerializerContext
{
}
