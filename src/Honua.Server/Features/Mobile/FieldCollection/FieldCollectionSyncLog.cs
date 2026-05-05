// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Mobile.FieldCollection;

/// <summary>
/// Source-generated logger for FieldCollection mobile sync endpoints (#894).
/// Keeps observability AOT-compatible and allocation-free.
/// </summary>
internal static partial class FieldCollectionSyncLog
{
    [LoggerMessage(
        EventId = 8940,
        Level = LogLevel.Debug,
        Message = "FieldCollection generation served. ServerGeneration={ServerGeneration}")]
    public static partial void GenerationServed(ILogger logger, long serverGeneration);

    [LoggerMessage(
        EventId = 8941,
        Level = LogLevel.Debug,
        Message = "FieldCollection sync cursor served. ClientId={ClientId} LastSyncGeneration={LastSyncGeneration}")]
    public static partial void SyncCursorServed(ILogger logger, string clientId, long lastSyncGeneration);

    [LoggerMessage(
        EventId = 8942,
        Level = LogLevel.Information,
        Message = "FieldCollection pull served. ClientId={ClientId} SinceGeneration={SinceGeneration} Limit={Limit} Returned={Returned} HasMore={HasMore} ServerGeneration={ServerGeneration}")]
    public static partial void PullServed(
        ILogger logger,
        string clientId,
        long sinceGeneration,
        int limit,
        int returned,
        bool hasMore,
        long serverGeneration);

    [LoggerMessage(
        EventId = 8943,
        Level = LogLevel.Information,
        Message = "FieldCollection push processed. ClientId={ClientId} ChangeId={ChangeId} FeatureId={FeatureId} LayerId={LayerId} Operation={Operation} Outcome={Outcome} ServerGeneration={ServerGeneration}")]
    public static partial void PushProcessed(
        ILogger logger,
        string clientId,
        string changeId,
        string featureId,
        int layerId,
        string operation,
        string outcome,
        long serverGeneration);

    [LoggerMessage(
        EventId = 8944,
        Level = LogLevel.Warning,
        Message = "FieldCollection push rejected. ChangeId={ChangeId} Reason={Reason}")]
    public static partial void PushRejected(ILogger logger, string changeId, string reason);
}
