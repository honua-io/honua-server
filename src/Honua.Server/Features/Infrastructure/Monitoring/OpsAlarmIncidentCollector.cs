// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class OpsAlarmIncidentCollector(
    IOpsIncidentObservationStore observations,
    IOpsIncidentEpisodeStore episodes) : IOpsExternalEventConsumer
{
    public async Task HandleAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(externalEvent.Body);
        var root = document.RootElement;
        if (Text(root, "source") != "aws.cloudwatch" ||
            Text(root, "detail-type") != "CloudWatch Alarm State Change")
        {
            return;
        }

        var observation = await observations.AppendAsync(CreateObservation(root, externalEvent), cancellationToken)
            .ConfigureAwait(false);

        // Replays use the immutable observation returned by the store, including after a partial write.
        await episodes.ObserveAsync(new OpsIncidentEpisodeSignal
        {
            AuthorityScope = observation.AuthorityScope,
            FindingId = observation.FindingId,
            ObservationId = observation.ObservationId,
            ObservedAt = observation.ObservedAt,
            State = observation.State switch
            {
                OpsIncidentObservationState.Active => OpsIncidentEpisodeState.Active,
                OpsIncidentObservationState.Recovered => OpsIncidentEpisodeState.Recovered,
                _ => OpsIncidentEpisodeState.Unknown
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static OpsIncidentObservation CreateObservation(JsonElement root, OpsExternalEvent externalEvent)
    {
        var alarmArn = AlarmResource(root);
        var detail = root.GetProperty("detail");
        var state = detail.TryGetProperty("state", out var stateElement)
            ? Text(stateElement, "value") : null;
        return new OpsIncidentObservation
        {
            AuthorityScope = "platform",
            SourceId = "aws.cloudwatch",
            ObservationId = externalEvent.Id,
            FindingId = OpsFindingId.Create("cloudwatch-alarm", alarmArn),
            ObservedAt = StateTimestamp(stateElement),
            State = state switch
            {
                "ALARM" => OpsIncidentObservationState.Active,
                "OK" => OpsIncidentObservationState.Recovered,
                _ => OpsIncidentObservationState.Unknown
            },
            EvidenceJson = new JsonObject
            {
                ["externalEventId"] = externalEvent.Id,
                ["alarmArn"] = alarmArn
            }.ToJsonString()
        };
    }

    private static string AlarmResource(JsonElement root)
    {
        if (root.TryGetProperty("resources", out var resources) &&
            resources.ValueKind == JsonValueKind.Array && resources.GetArrayLength() == 1 &&
            resources[0].ValueKind == JsonValueKind.String && resources[0].GetString() is { } arn)
        {
            var parts = arn.Split(':', 7);
            if (parts.Length == 7 && parts[0] == "arn" && parts[1].Length > 0 &&
                parts[2] == "cloudwatch" && parts[3].Length > 0 && parts[4].Length == 12 &&
                parts[4].All(char.IsAsciiDigit) && parts[5] == "alarm" && parts[6].Length > 0)
            {
                return arn;
            }
        }

        throw new JsonException("CloudWatch alarm state changes require exactly one alarm resource ARN.");
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset StateTimestamp(JsonElement state)
    {
        var timestamp = Text(state, "timestamp");
        if (timestamp is { Length: >= 5 } && timestamp[^5] is '+' or '-')
        {
            timestamp = timestamp.Insert(timestamp.Length - 2, ":");
        }
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (DateTimeOffset.TryParseExact(timestamp, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var observedAt))
        {
            return observedAt;
        }
        throw new JsonException("CloudWatch alarm state changes require a valid state timestamp with a UTC offset.");
    }
}
