// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.CloudDemo;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CloudDemoProblemResponse))]
[JsonSerializable(typeof(CloudDemoResetResponse))]
[JsonSerializable(typeof(CloudDemoRealtimeCheckpoint))]
[JsonSerializable(typeof(CloudDemoRealtimeStatusEvent))]
[JsonSerializable(typeof(CloudDemoRealtimeHeartbeatEvent))]
[JsonSerializable(typeof(CloudDemoRealtimeSnapshotEvent))]
[JsonSerializable(typeof(CloudDemoRealtimeFeaturePatch))]
[JsonSerializable(typeof(CloudDemoIncidentFeature))]
[JsonSerializable(typeof(CloudDemoIncidentFeature[]))]
[JsonSerializable(typeof(CloudDemoIncidentRelatedRecord))]
[JsonSerializable(typeof(CloudDemoIncidentRelatedRecord[]))]
[JsonSerializable(typeof(CloudDemoIncidentAttachment))]
[JsonSerializable(typeof(CloudDemoIncidentAttachment[]))]
internal sealed partial class CloudDemoJsonContext : JsonSerializerContext;

internal sealed record CloudDemoProblemResponse(string Code, string Message);

internal sealed record CloudDemoResetResponse(
    string State,
    string ServiceId,
    int LayerId,
    DateTimeOffset ResetAt);

internal sealed record CloudDemoRealtimeCheckpoint(
    string Cursor,
    string Watermark,
    string Timestamp,
    long Sequence);

internal sealed record CloudDemoRealtimeStatusEvent(
    string Type,
    string Status,
    string EventId,
    string Cursor,
    string Watermark,
    string Timestamp,
    long Sequence,
    CloudDemoRealtimeCheckpoint Checkpoint,
    string? Reason = null,
    int? RetryAfterMs = null);

internal sealed record CloudDemoRealtimeHeartbeatEvent(
    string Type,
    string EventId,
    string Cursor,
    string Watermark,
    string Timestamp,
    long Sequence,
    CloudDemoRealtimeCheckpoint Checkpoint);

internal sealed record CloudDemoRealtimeSnapshotEvent(
    string Type,
    string EventId,
    CloudDemoRealtimeFeaturePatch[] Features,
    bool Replace,
    string Cursor,
    string Watermark,
    string Timestamp,
    long Sequence,
    CloudDemoRealtimeCheckpoint Checkpoint);

internal sealed record CloudDemoRealtimeFeaturePatch(
    string Id,
    string SourceId,
    CloudDemoIncidentFeature Feature,
    long Version,
    string UpdatedAt);

internal sealed record CloudDemoIncidentFeature(
    string Id,
    string Title,
    string Type,
    string Severity,
    string Status,
    string AssignedTo,
    string UpdatedAt,
    string ReportedAt,
    double[] Coordinate,
    int EtaMinutes,
    int AffectedAssets,
    string Summary,
    CloudDemoIncidentRelatedRecord[] RelatedRecords,
    CloudDemoIncidentAttachment[] Attachments);

internal sealed record CloudDemoIncidentRelatedRecord(
    string Id,
    string Label,
    string Status);

internal sealed record CloudDemoIncidentAttachment(
    string Id,
    string Name,
    string Kind);
