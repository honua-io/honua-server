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
/// Source-generated JSON metadata for alert admin payloads.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AlertZoneRequest))]
[JsonSerializable(typeof(AlertZoneResponse))]
[JsonSerializable(typeof(AlertZoneResponse[]))]
[JsonSerializable(typeof(AlertRuleRequest))]
[JsonSerializable(typeof(AlertRuleResponse))]
[JsonSerializable(typeof(AlertRuleResponse[]))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertZoneResponse[]>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertRuleResponse[]>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class AlertAdminJsonContext : JsonSerializerContext
{
}
