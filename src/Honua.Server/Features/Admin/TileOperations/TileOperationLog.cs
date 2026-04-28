// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Server.Features.Admin.TileOperations;

internal static partial class TileOperationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected exception while processing tile job {JobId}.")]
    public static partial void BackgroundJobProcessingFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tile background worker faulted and will restart.")]
    public static partial void BackgroundWorkerRestarting(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist missing-request status for tile operation job {JobId}.")]
    public static partial void MissingRequestStatusPersistenceFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping tile-operation recovery for job {JobId} because the Redis claim key could not be inspected.")]
    public static partial void RecoveryClaimInspectionFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tile-operation lease renewal was lost while processing a background tile job.")]
    public static partial void LeaseRenewalLost(ILogger logger);

    [LoggerMessage(EventId = 9210, Level = LogLevel.Information, Message = "Publishing PMTiles artifact for tile job {JobId} to object key {ObjectKey}.")]
    public static partial void PublishUploadStart(ILogger logger, string jobId, string objectKey);

    [LoggerMessage(EventId = 9211, Level = LogLevel.Information, Message = "Published PMTiles artifact for tile job {JobId} ({Size} bytes) at {ObjectKey} via {UrlStrategy}.")]
    public static partial void PublishUploadComplete(ILogger logger, string jobId, string objectKey, long size, PMTilesUrlStrategy urlStrategy);
}
