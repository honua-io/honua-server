// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Server.Features.Admin.TileOperations;

internal static partial class TileOperationLog
{
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

    [LoggerMessage(EventId = 9217, Level = LogLevel.Information, Message = "Resuming tile-cache generation {GenerationId} from {CompletedBlocks} completed metatile blocks with {FailedUnitCount} failed units (attempt {Attempt}).")]
    public static partial void GenerationResumed(ILogger logger, string generationId, int completedBlocks, int failedUnitCount, int attempt);

    [LoggerMessage(EventId = 9218, Level = LogLevel.Warning, Message = "Failed to load the tile-cache generation checkpoint for generation {GenerationId}; seeding the full grid.")]
    public static partial void CheckpointLoadFailed(ILogger logger, string generationId, Exception exception);

    [LoggerMessage(EventId = 9219, Level = LogLevel.Warning, Message = "Failed to persist the tile-cache generation checkpoint for generation {GenerationId}; resume state may be stale.")]
    public static partial void CheckpointSaveFailed(ILogger logger, string generationId, Exception exception);

    [LoggerMessage(EventId = 9220, Level = LogLevel.Warning, Message = "Failed to delete the completed tile-cache generation checkpoint for generation {GenerationId}; it will self-expire.")]
    public static partial void CheckpointDeleteFailed(ILogger logger, string generationId, Exception exception);
}
