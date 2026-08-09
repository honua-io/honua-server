// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// Source-generated logging for the controlled-conformance workflow. Every controlled write
/// is logged with its run identity so the mutation surface is auditable (NFR-002); no run
/// token or caller-supplied free text is ever logged.
/// </summary>
internal static partial class FeatureStreamConformanceLog
{
    [LoggerMessage(
        EventId = 5140,
        Level = LogLevel.Information,
        Message = "Conformance run {RunId} leased against {ServiceId}/{LayerId} until {ExpiresAt}.")]
    public static partial void RunLeased(ILogger logger, Guid runId, string serviceId, int layerId, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 5141,
        Level = LogLevel.Information,
        Message = "Conformance run {RunId} applied controlled {Operation} on object {ObjectId}.")]
    public static partial void MutationApplied(ILogger logger, Guid runId, string operation, long objectId);

    [LoggerMessage(
        EventId = 5142,
        Level = LogLevel.Information,
        Message = "Conformance run {RunId} released; {DeletedRecords} controlled record(s) deleted.")]
    public static partial void RunReleased(ILogger logger, Guid runId, int deletedRecords);

    [LoggerMessage(
        EventId = 5143,
        Level = LogLevel.Warning,
        Message = "Conformance sweep reclaimed {ReclaimedRuns} expired lease(s) and deleted {DeletedRecords} orphaned controlled record(s).")]
    public static partial void RecordsSwept(ILogger logger, int reclaimedRuns, int deletedRecords);

    [LoggerMessage(
        EventId = 5144,
        Level = LogLevel.Warning,
        Message = "Conformance source reset: {ReleasedRuns} lease(s) dropped and {DeletedRecords} controlled record(s) deleted.")]
    public static partial void SourceReset(ILogger logger, int releasedRuns, int deletedRecords);

    [LoggerMessage(
        EventId = 5145,
        Level = LogLevel.Error,
        Message = "Configured conformance service '{ServiceId}' has no '{RunIdField}' field; the controlled-mutation surface stays closed because record ownership could not be recorded.")]
    public static partial void SourceMissingMarkerField(ILogger logger, string serviceId, string runIdField);

    [LoggerMessage(
        EventId = 5146,
        Level = LogLevel.Warning,
        Message = "Failed to publish the feature-change event for controlled mutation on layer {LayerId} object {ObjectId}.")]
    public static partial void PublishFailed(ILogger logger, int layerId, long objectId, Exception exception);

    [LoggerMessage(
        EventId = 5147,
        Level = LogLevel.Error,
        Message = "Conformance TTL sweep failed; orphaned controlled records remain until the next sweep.")]
    public static partial void SweepFailed(ILogger logger, Exception exception);
}
