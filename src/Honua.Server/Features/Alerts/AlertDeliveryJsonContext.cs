// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Alerts;

internal sealed record DigestAlertPayloadItem
{
    public required string DedupeKey { get; init; }
    public required long RuleId { get; init; }
    public long? ZoneId { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public required string TriggerType { get; init; }
    public required string Severity { get; init; }
    public required string IncidentStatus { get; init; }
    public required long IncidentDurationMs { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Payload { get; init; }
}

internal sealed record SlackAlertPayload
{
    [JsonPropertyName("attachments")]
    public required SlackAlertAttachment[] Attachments { get; init; }
}

internal sealed record SlackAlertAttachment
{
    [JsonPropertyName("color")]
    public required string Color { get; init; }

    [JsonPropertyName("fallback")]
    public required string Fallback { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("footer")]
    public required string Footer { get; init; }

    [JsonPropertyName("ts")]
    public required long Timestamp { get; init; }
}

internal sealed record TeamsAlertPayload
{
    [JsonPropertyName("@type")]
    public required string Type { get; init; }

    [JsonPropertyName("@context")]
    public required string Context { get; init; }

    [JsonPropertyName("themeColor")]
    public required string ThemeColor { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("sections")]
    public required TeamsAlertSection[] Sections { get; init; }
}

internal sealed record TeamsAlertSection
{
    [JsonPropertyName("activityTitle")]
    public required string ActivityTitle { get; init; }

    [JsonPropertyName("activitySubtitle")]
    public required string ActivitySubtitle { get; init; }

    [JsonPropertyName("facts")]
    public required TeamsAlertFact[] Facts { get; init; }

    [JsonPropertyName("markdown")]
    public required bool Markdown { get; init; }
}

internal sealed record TeamsAlertFact
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(DigestAlertPayloadItem[]))]
[JsonSerializable(typeof(SlackAlertPayload))]
[JsonSerializable(typeof(TeamsAlertPayload))]
internal sealed partial class AlertDeliveryJsonContext : JsonSerializerContext
{
}
