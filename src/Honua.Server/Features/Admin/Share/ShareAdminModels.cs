// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Share;

internal sealed record ShareExportDefinitionRequest
{
    public string? ResourceId { get; init; }
    public required string ServiceName { get; init; }
    public required int LayerId { get; init; }
    public string? DisplayName { get; init; }
    public required string DestinationType { get; init; }
    public Dictionary<string, string> DestinationConfig { get; init; } = new(StringComparer.Ordinal);
    public required string Format { get; init; }
    public required string Schedule { get; init; }
    public string? ScheduleState { get; init; }
}

internal sealed record ShareExportDefinitionPageResponse
{
    public required ShareExportDefinitionResponse[] Items { get; init; }
    public string? NextCursor { get; init; }
}

internal sealed record ShareExportDefinitionResponse
{
    public required string ExportId { get; init; }
    public string? ResourceId { get; init; }
    public required string ServiceName { get; init; }
    public required int LayerId { get; init; }
    public string? DisplayName { get; init; }
    public required string DestinationType { get; init; }
    public required string DestinationStatus { get; init; }
    public required Dictionary<string, string> DestinationConfig { get; init; }
    public required string Format { get; init; }
    public required string Schedule { get; init; }
    public required string ScheduleState { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
}

internal sealed record ShareExportRunPageResponse
{
    public required ShareExportRunResponse[] Items { get; init; }
    public string? NextCursor { get; init; }
}

internal sealed record ShareExportRunResponse
{
    public required string RunId { get; init; }
    public required string ExportId { get; init; }
    public required string TriggerKind { get; init; }
    public required string Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? JobRunId { get; init; }
    public required DateTimeOffset TriggeredAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? TargetSummary { get; init; }
    public required string[] ResultArtifacts { get; init; }
    public string? LastError { get; init; }
}

internal sealed record ShareTrafficSummaryResponse
{
    public ShareItemRefResponse? ItemRef { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required ShareTrafficCountsResponse ByInteractionType { get; init; }
    public long TotalRequests { get; init; }
}

internal sealed record ShareTrafficSeriesResponse
{
    public ShareItemRefResponse? ItemRef { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required TimeSpan BucketDuration { get; init; }
    public required ShareTrafficBucketResponse[] Buckets { get; init; }
}

internal sealed record ShareTrafficBucketResponse
{
    public required DateTimeOffset BucketStart { get; init; }
    public required ShareTrafficCountsResponse ByInteractionType { get; init; }
    public long Total { get; init; }
}

internal sealed record ShareTrafficCountsResponse
{
    public long Public { get; init; }
    public long PublicLink { get; init; }
    public long Embed { get; init; }
    public long OpenData { get; init; }
    public long Dcat { get; init; }
    public long Stac { get; init; }
    public long Export { get; init; }
}

internal sealed record ShareItemRefResponse
{
    public string? ResourceId { get; init; }
    public required string ServiceName { get; init; }
    public required int LayerId { get; init; }
}
