// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal sealed class OperateObservabilityFixtureSeedResponse
{
    public required string Profile { get; init; }

    public required bool SourceHydrated { get; init; }

    public required DateTimeOffset SeededAt { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required string CorrelationId { get; init; }

    public required string TraceId { get; init; }

    public required string Actor { get; init; }

    public required OperateObservabilityAlertFixtureSeed Alerts { get; init; }

    public required OperateObservabilityJobFixtureSeed[] Jobs { get; init; }

    public required OperateObservabilityInvestigationFixtureSeed Investigation { get; init; }

    public required Dictionary<string, string> Links { get; init; }
}

internal sealed class OperateObservabilityAlertFixtureSeed
{
    /// <summary>
    /// Sentinel returned when the geofence alert stores are not registered (alerts.geofence
    /// disabled). Zero ids and empty collections tell the Console observability page to render
    /// the alert surface in its disabled/empty state rather than treating the seed as failed.
    /// </summary>
    public static OperateObservabilityAlertFixtureSeed Skipped { get; } = new()
    {
        ZoneId = 0,
        ZoneName = string.Empty,
        RuleId = 0,
        RuleName = string.Empty,
        OpenAlertEventId = 0,
        ResolvedAlertEventId = 0,
        DedupeKeys = []
    };

    public required long ZoneId { get; init; }

    public required string ZoneName { get; init; }

    public required long RuleId { get; init; }

    public required string RuleName { get; init; }

    public required long OpenAlertEventId { get; init; }

    public required long ResolvedAlertEventId { get; init; }

    public required string[] DedupeKeys { get; init; }
}

internal sealed class OperateObservabilityJobFixtureSeed
{
    public required string JobId { get; init; }

    public required string Status { get; init; }

    public required string DetailLink { get; init; }

    public required string LogsLink { get; init; }

    public required string ArtifactsLink { get; init; }
}

internal sealed class OperateObservabilityInvestigationFixtureSeed
{
    public required string InvestigationId { get; init; }

    public required long[] PinIds { get; init; }

    public required long[] LinkIds { get; init; }

    public required string DetailLink { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OperateObservabilityFixtureSeedResponse))]
[JsonSerializable(typeof(OperateObservabilityAlertFixtureSeed))]
[JsonSerializable(typeof(OperateObservabilityJobFixtureSeed))]
[JsonSerializable(typeof(OperateObservabilityJobFixtureSeed[]))]
[JsonSerializable(typeof(OperateObservabilityInvestigationFixtureSeed))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class OperateObservabilityFixtureJsonContext : JsonSerializerContext
{
}
