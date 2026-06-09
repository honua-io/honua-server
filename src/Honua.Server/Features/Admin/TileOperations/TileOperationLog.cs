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

    [LoggerMessage(EventId = 9212, Level = LogLevel.Warning, Message = "Failed to delete orphan PMTiles publish artifact {ArtifactId} for tile job {JobId} after access URL generation failure.")]
    public static partial void PublishOrphanCleanupFailed(ILogger logger, string jobId, string artifactId, Exception exception);

    [LoggerMessage(EventId = 9213, Level = LogLevel.Warning, Message = "Cloud storage reported failure deleting orphan PMTiles publish artifact {ArtifactId} for tile job {JobId} after access URL generation failure.")]
    public static partial void PublishOrphanCleanupReturnedFalse(ILogger logger, string jobId, string artifactId);

    [LoggerMessage(EventId = 9214, Level = LogLevel.Warning, Message = "Publish access URL generation failed for tile job {JobId} (artifact {ArtifactId}, strategy {UrlStrategy}).")]
    public static partial void PublishAccessUrlFailed(ILogger logger, string jobId, string artifactId, PMTilesUrlStrategy urlStrategy, Exception exception);

    [LoggerMessage(EventId = 9215, Level = LogLevel.Warning, Message = "Retained overwritten PMTiles publish artifact {ArtifactId} for tile job {JobId} (strategy {UrlStrategy}) after access URL generation failure to preserve the previously published bytes.")]
    public static partial void PublishOverwriteRetained(ILogger logger, string jobId, string artifactId, PMTilesUrlStrategy urlStrategy);

    [LoggerMessage(EventId = 9216, Level = LogLevel.Warning, Message = "Tile generation failed for layer {LayerId} tile {Z}/{X}/{Y}.")]
    public static partial void TileGenerationFailed(
        ILogger logger,
        int layerId,
        int z,
        int x,
        int y,
        Exception exception);
}
