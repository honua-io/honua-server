// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsWatchdogEventMapper
{
    private const int MaxMessageLength = 1024;
    private const int MaxStageLength = 128;

    internal static OpsFinding? Map(JsonElement envelope)
    {
        if (Text(envelope, "source") != "honua.watchdog" ||
            Text(envelope, "detail-type") != "Honua watchdog incident" ||
            !envelope.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object ||
            !detail.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
        {
            return null;
        }

        var id = Text(envelope, "id");
        var incidentId = Text(detail, "incidentId");
        var stage = Text(detail, "stage");
        var message = Text(detail, "message");
        var severity = Severity(Text(detail, "severity"));
        var resource = ServiceResource(envelope);
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(incidentId, out _) ||
            string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageLength || stage.Length > MaxStageLength ||
            resource is null || severity is null || !KnownStage(stage) ||
            !DateTimeOffset.TryParse(Text(envelope, "time"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var occurredAt))
        {
            return null;
        }

        return new OpsFinding
        {
            Id = OpsFindingId.Create("watchdog-incident", id),
            Rule = "watchdog-incident",
            Severity = severity.Value,
            Title = $"Watchdog incident: {stage}",
            Explanation = message,
            DetectedAt = occurredAt.ToUniversalTime(),
            Subject = new OpsFindingSubject { TargetId = resource, OperationId = incidentId },
            EvidenceRefs = [$"aws-event:{id}"],
        };
    }

    internal static int StageOrder(OpsFinding finding) => finding.Title switch
    {
        "Watchdog incident: recovered" => 6,
        "Watchdog incident: needs_person" => 5,
        var title when title.StartsWith("Watchdog incident: action:", StringComparison.Ordinal) => 4,
        var title when title.StartsWith("Watchdog incident: diagnosed:", StringComparison.Ordinal) => 3,
        "Watchdog incident: probe_blocked" or "Watchdog incident: probe_unexpected" => 2,
        _ => 0,
    };

    private static OpsFindingSeverity? Severity(string severity) => severity switch
    {
        "Info" => OpsFindingSeverity.Info,
        "Warning" => OpsFindingSeverity.Warning,
        "Critical" => OpsFindingSeverity.Critical,
        _ => null,
    };

    private static bool KnownStage(string stage) =>
        stage is "observed" or "probe_blocked" or "probe_unexpected" or "needs_person" or "recovered" ||
        stage.StartsWith("diagnosed:", StringComparison.Ordinal) && stage.Length > "diagnosed:".Length ||
        stage is "action:requested" or "action:unknown" or "action:rate_limited" or "action:suppressed";

    private static string? ServiceResource(JsonElement envelope) =>
        envelope.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array
            ? resources.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .FirstOrDefault(value => value is not null && value.StartsWith("arn:", StringComparison.Ordinal) &&
                    value.Contains(":ecs:", StringComparison.Ordinal) && value.Contains(":service/", StringComparison.Ordinal))
            : null;

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
}
