// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Shared tile-operation execution engine for seed/warm/invalidate/purge/archive/publish.
/// Extracted from <see cref="TileOperationJobService"/> so the legacy in-process channel
/// worker and the durable <see cref="TileCacheJobExecutor"/> (issue #1697) run identical
/// query/filter/geodesy/PMTiles/upload behavior, progress reporting, and telemetry — the
/// only difference is the maximum tile count: the in-process path caps at 5,000 to protect
/// the serving pod, while the batch-dispatched path raises the ceiling because the work runs
/// on dedicated compute bounded by backend timeout/resources.
/// </summary>
internal sealed partial class TileOperationExecutionCore
{
    private readonly IUniversalProgressStore _progressStore;
    private readonly OutputCacheInvalidationService _cacheInvalidationService;
    private readonly TileOptions _tileOptions;
    private readonly TileLimits _tileLimits;
    private readonly ILogger _logger;
    private readonly int _maxTilesCeiling;

    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    public TileOperationExecutionCore(
        IUniversalProgressStore progressStore,
        OutputCacheInvalidationService cacheInvalidationService,
        IOptions<TileOptions> tileOptions,
        IOptions<LimitsOptions> limitsOptions,
        ILogger logger,
        int maxTilesCeiling)
    {
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _cacheInvalidationService = cacheInvalidationService ?? throw new ArgumentNullException(nameof(cacheInvalidationService));
        _tileOptions = tileOptions?.Value ?? throw new ArgumentNullException(nameof(tileOptions));
        _tileLimits = limitsOptions?.Value?.Tiles ?? throw new ArgumentNullException(nameof(limitsOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxTilesCeiling = Math.Max(1, maxTilesCeiling);
    }

    /// <summary>
    /// Runs the requested tile operation against the supplied request scope using the
    /// metadata/tile/cloud-storage services resolved from <paramref name="serviceProvider"/>.
    /// Returns the terminal <see cref="TileOperationProgress"/> for the job.
    /// </summary>
    public async Task<TileOperationProgress> ExecuteAsync(
        TileOperationProgress started,
        TileOperationStartRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var graphProvider = serviceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var tileProvider = serviceProvider.GetRequiredService<ITileProvider>();

        return request.Operation switch
        {
            "seed" => await ExecuteSeedOrWarmAsync(started, request, warmMode: false, graphProvider, tileProvider, cancellationToken).ConfigureAwait(false),
            "warm" => await ExecuteSeedOrWarmAsync(started, request, warmMode: true, graphProvider, tileProvider, cancellationToken).ConfigureAwait(false),
            "invalidate" => await ExecuteInvalidationAsync(started, request, graphProvider, cancellationToken).ConfigureAwait(false),
            "purge" => await ExecuteInvalidationAsync(started, request, graphProvider, cancellationToken).ConfigureAwait(false),
            "archive" => await ExecuteArchiveAsync(started, request, tileProvider, serviceProvider, cancellationToken).ConfigureAwait(false),
            "publish" => await ExecutePublishAsync(started, request, tileProvider, serviceProvider, cancellationToken).ConfigureAwait(false),
            _ => started with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Unsupported tile operation '{request.Operation}'.",
                CurrentPhase = "Failed"
            }
        };
    }

    private async Task<TileOperationProgress> ExecuteInvalidationAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        if (request.LayerId.HasValue)
        {
            await _cacheInvalidationService.InvalidateLayerAsync(request.ServiceId, request.LayerId.Value, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            var layerIds = await ResolveServiceLayerIdsAsync(graphProvider, request.ServiceId, cancellationToken).ConfigureAwait(false);
            await _cacheInvalidationService.InvalidateServiceCatalogAsync(request.ServiceId, layerIds, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _cacheInvalidationService.InvalidateOgcMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        return progress with
        {
            Status = OperationStatus.Completed,
            TotalTiles = 1,
            ProcessedTiles = 1,
            SuccessfulTiles = 1,
            FailedTiles = 0,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = $"{request.Operation} completed"
        };
    }

    private async Task<TileOperationProgress> ExecuteSeedOrWarmAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        bool warmMode,
        IMetadataV2GraphProvider graphProvider,
        ITileProvider tileProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        var layerIds = await ResolveLayerIdsAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        if (layerIds.Count == 0)
        {
            throw new InvalidOperationException("Unable to resolve a target layer for tile operation.");
        }

        var tileCoordinates = BuildTileCoordinates(request);
        if (tileCoordinates.Count == 0)
        {
            throw new InvalidOperationException("Tile operation produced no target tiles.");
        }

        var total = (long)layerIds.Count * tileCoordinates.Count;
        var processed = 0L;
        var successful = 0L;
        var failed = 0L;
        var warningList = new List<string>();
        var phase = warmMode ? "Warming tiles" : "Seeding tiles";

        var current = progress with
        {
            TotalTiles = total,
            CurrentPhase = phase
        };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        // Metatiling (#1837): render tiles in aligned N×N metatile blocks per pass so neighbouring
        // tiles are produced together. With MetatileFactor=1 this degenerates to one tile per block
        // (the original per-tile order). The provider still renders one tile at a time, but the
        // block-coherent iteration amortizes setup and keeps adjacent tiles' caches warm together.
        var metatiles = MetatileGrouping.Group(
            tileCoordinates.Select(static c => new TileIndex(c.Z, c.X, c.Y)),
            _tileOptions.MetatileFactor);

        foreach (var layerId in layerIds)
        {
            foreach (var metatile in metatiles)
            {
                foreach (var member in metatile.Tiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await tileProvider.GetMvtTileAsync(
                            layerId,
                            member.X,
                            member.Y,
                            member.Z,
                            query: null,
                            _tileOptions,
                            _tileLimits,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        successful++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        TileOperationLog.TileGenerationFailed(_logger, layerId, member.Z, member.X, member.Y, ex);
                        failed++;
                        if (warningList.Count < 20)
                        {
                            warningList.Add($"Layer {layerId} tile {member.Z}/{member.X}/{member.Y}: tile generation failed.");
                        }
                    }

                    processed++;
                    if (processed % 25 == 0 || processed == total)
                    {
                        current = current with
                        {
                            ProcessedTiles = processed,
                            SuccessfulTiles = successful,
                            FailedTiles = failed,
                            Warnings = warningList.ToArray(),
                            CurrentPhase = $"{phase} ({processed}/{total})"
                        };
                        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        if (processed > 0)
        {
            TileOperationMetrics.TileThroughput.Add(
                processed,
                new TagList
                {
                    { "operation", request.Operation }
                });
        }

        var completedStatus = failed == 0 ? OperationStatus.Completed : OperationStatus.Failed;
        return current with
        {
            Status = completedStatus,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = failed == 0 ? null : $"{failed} tiles failed to process.",
            CurrentPhase = failed == 0 ? $"{request.Operation} completed" : $"{request.Operation} completed with failures"
        };
    }

    private async Task<TileOperationProgress> ExecuteArchiveAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        ITileProvider tileProvider,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        if (!request.LayerId.HasValue)
        {
            throw new InvalidOperationException("Archive operations require a layerId.");
        }

        var layerId = request.LayerId.Value;

        var build = await BuildPMTilesArchiveAsync("archive", progress, request, layerId, tileProvider, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archiveStream = build.ArchiveStream!;

        var current = build.Progress with { CurrentPhase = "Uploading archive" };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        var cloudStorage = serviceProvider.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Cloud storage is not configured. Archive operations require cloud storage.",
                CurrentPhase = "Failed"
            };
        }

        var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
        {
            Content = archiveStream,
            FileName = $"{current.JobId}.pmtiles",
            ContentType = "application/vnd.pmtiles",
            SizeBytes = build.ArchiveSize,
            TimeToLive = TimeSpan.FromHours(24),
            Folder = "pmtiles",
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("jobId", current.JobId)
                .Add("operation", "archive")
        }, cancellationToken).ConfigureAwait(false);

        if (!uploadResult.Success)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Archive upload failed: {uploadResult.ErrorMessage ?? "unknown error"}.",
                CurrentPhase = "Failed"
            };
        }

        var archiveFileId = uploadResult.File?.FileId;
        if (string.IsNullOrWhiteSpace(archiveFileId))
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Archive upload succeeded but returned no file ID.",
                CurrentPhase = "Failed"
            };
        }

        var downloadUrl = await cloudStorage.GetPresignedUrlAsync(
            archiveFileId,
            TimeSpan.FromHours(24),
            cancellationToken).ConfigureAwait(false);

        TileOperationMetrics.ArchivesGenerated.Add(1);
        TileOperationMetrics.ArchiveSizeBytes.Record(build.ArchiveSize);

        var completedStatus = build.Failed > 0 ? OperationStatus.Failed : OperationStatus.Completed;
        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            ArchiveFileId = archiveFileId,
            DownloadUrl = downloadUrl,
            Status = completedStatus,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = build.Failed > 0 ? $"{build.Failed} tiles failed during generation." : null,
            CurrentPhase = build.Failed > 0 ? "Archive completed with failures" : "Archive generation completed"
        };
    }

    private async Task<TileOperationProgress> ExecutePublishAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        ITileProvider tileProvider,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        if (!request.LayerId.HasValue)
        {
            throw new InvalidOperationException("Publish operations require a layerId.");
        }

        var layerId = request.LayerId.Value;

        var cloudOptions = serviceProvider.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var publishOptions = cloudOptions.PMTilesPublish ?? new PMTilesPublishOptions();

        // Pre-flight: validate URL strategy configuration before generating or uploading
        // a permanent artifact, so a misconfigured PublicUrl never produces an orphan.
        if (publishOptions.UrlStrategy == PMTilesUrlStrategy.PublicUrl &&
            string.IsNullOrWhiteSpace(publishOptions.PublicBucketBaseUrl))
        {
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "PMTilesPublish:PublicBucketBaseUrl must be configured when UrlStrategy is PublicUrl.",
                CurrentPhase = "Failed"
            };
        }

        var build = await BuildPMTilesArchiveAsync("publish", progress, request, layerId, tileProvider, cancellationToken).ConfigureAwait(false);
        if (!build.HasArchive)
        {
            return build.Progress;
        }

        await using var archiveStream = build.ArchiveStream!;

        // Durable publish writes to a deterministic key that may already host a
        // previously good artifact. A partial generation (failed > 0) would
        // overwrite that artifact with bytes missing the failed tiles, which is
        // a silent data-loss for active clients. Refuse to upload in that case
        // and fail the job so operators retry once the upstream is healthy.
        if (build.Failed > 0)
        {
            return build.Progress with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Publish aborted before upload: {build.Failed} tiles failed during generation.",
                CurrentPhase = "Failed"
            };
        }

        var cloudStorage = serviceProvider.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            return build.Progress with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Cloud storage is not configured. Publish operations require cloud storage.",
                CurrentPhase = "Failed"
            };
        }

        var providerKeyPrefix = cloudOptions.Provider switch
        {
            CloudStorageProvider.AwsS3 => cloudOptions.AwsS3?.KeyPrefix,
            CloudStorageProvider.AzureBlob => cloudOptions.AzureBlob?.BlobPrefix,
            _ => null
        };

        var objectKey = BuildPublishObjectKey(
            providerKeyPrefix,
            publishOptions.KeyPrefix,
            request.ServiceId,
            layerId,
            request.TileMatrixSetId);

        var current = build.Progress with { CurrentPhase = "Uploading durable PMTiles artifact" };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);
        TileOperationLog.PublishUploadStart(_logger, current.JobId, objectKey);

        var keyAlreadyExisted = await cloudStorage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false);

        var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
        {
            Content = archiveStream,
            FileName = $"{Path.GetFileName(objectKey)}",
            ContentType = "application/vnd.pmtiles",
            SizeBytes = build.ArchiveSize,
            TimeToLive = null,
            ObjectKeyOverride = objectKey,
            Metadata = ImmutableDictionary<string, string>.Empty
                .Add("jobId", current.JobId)
                .Add("operation", "publish")
                .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
        }, cancellationToken).ConfigureAwait(false);

        if (!uploadResult.Success || uploadResult.File is null)
        {
            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"Publish upload failed: {uploadResult.ErrorMessage ?? "unknown error"}.",
                CurrentPhase = "Failed"
            };
        }

        var bucket = ResolvePublishBucket(cloudOptions);
        var artifactId = uploadResult.File.FileId;

        string accessUrl;
        DateTimeOffset? accessUrlExpiresAt;
        try
        {
            (accessUrl, accessUrlExpiresAt) = await ResolvePublishAccessUrlAsync(
                cloudStorage,
                publishOptions,
                artifactId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TileOperationLog.PublishAccessUrlFailed(_logger, current.JobId, artifactId, publishOptions.UrlStrategy, ex);
            if (!keyAlreadyExisted)
            {
                await TryDeletePublishArtifactAsync(cloudStorage, artifactId, current.JobId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                TileOperationLog.PublishOverwriteRetained(_logger, current.JobId, artifactId, publishOptions.UrlStrategy);
            }

            return current with
            {
                ArchiveSizeBytes = build.ArchiveSize,
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Publish access URL generation failed.",
                CurrentPhase = "Failed"
            };
        }

        var descriptor = new PMTilesArtifactDescriptor
        {
            ArtifactId = artifactId,
            StorageProvider = uploadResult.File.Provider,
            Bucket = bucket,
            ObjectKey = uploadResult.File.StoragePath,
            ContentType = uploadResult.File.ContentType,
            SizeBytes = uploadResult.File.SizeBytes,
            UrlStrategy = publishOptions.UrlStrategy,
            AccessUrl = accessUrl,
            AccessUrlExpiresAt = accessUrlExpiresAt,
            PublishedAt = uploadResult.File.UploadedAt,
            MinZoom = build.MinZoom,
            MaxZoom = build.MaxZoom,
            Bounds = [build.MinLon, build.MinLat, build.MaxLon, build.MaxLat],
            LayerId = layerId,
            ServiceId = request.ServiceId,
            TileMatrixSetId = request.TileMatrixSetId
        };

        TileOperationMetrics.ArchivesGenerated.Add(1);
        TileOperationMetrics.ArchiveSizeBytes.Record(build.ArchiveSize);
        TileOperationLog.PublishUploadComplete(_logger, current.JobId, descriptor.ObjectKey, build.ArchiveSize, descriptor.UrlStrategy);

        return current with
        {
            ArchiveSizeBytes = build.ArchiveSize,
            PublishedArtifact = descriptor,
            Status = OperationStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = null,
            CurrentPhase = "Publish completed"
        };
    }

    private async Task<PMTilesBuildResult> BuildPMTilesArchiveAsync(
        string operationName,
        TileOperationProgress progress,
        TileOperationStartRequest request,
        int layerId,
        ITileProvider tileProvider,
        CancellationToken cancellationToken)
    {
        var tileCoordinates = BuildTileCoordinates(request);
        if (tileCoordinates.Count == 0)
        {
            throw new InvalidOperationException($"{operationName} operation produced no target tiles.");
        }

        var total = (long)tileCoordinates.Count;
        long processed = 0;
        long successful = 0;
        long failed = 0;
        var warningList = new List<string>();

        var current = progress with
        {
            TotalTiles = total,
            CurrentPhase = $"Generating tiles for {operationName}"
        };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        var writer = new PMTilesWriter(tileCompression: PMTilesCompression.None);
        foreach (var coordinate in tileCoordinates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tileData = await tileProvider.GetMvtTileAsync(
                    layerId,
                    coordinate.X,
                    coordinate.Y,
                    coordinate.Z,
                    query: null,
                    _tileOptions,
                    _tileLimits,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (tileData is { Length: > 0 })
                {
                    writer.AddTile(coordinate.Z, coordinate.X, coordinate.Y, tileData);
                    successful++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TileOperationLog.TileGenerationFailed(_logger, layerId, coordinate.Z, coordinate.X, coordinate.Y, ex);
                failed++;
                if (warningList.Count < 20)
                {
                    warningList.Add($"Layer {layerId} tile {coordinate.Z}/{coordinate.X}/{coordinate.Y}: tile archive generation failed.");
                }
            }

            processed++;
            if (processed % 25 == 0 || processed == total)
            {
                current = current with
                {
                    ProcessedTiles = processed,
                    SuccessfulTiles = successful,
                    FailedTiles = failed,
                    Warnings = warningList.ToArray(),
                    CurrentPhase = $"Generating tiles ({processed}/{total})"
                };
                await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);
            }
        }

        if (processed > 0)
        {
            TileOperationMetrics.TileThroughput.Add(processed, new TagList { { "operation", operationName } });
        }

        if (writer.TileCount == 0)
        {
            return PMTilesBuildResult.Empty(current with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = $"No tiles were generated for the {operationName}.",
                CurrentPhase = "Failed"
            });
        }

        current = current with { CurrentPhase = "Writing PMTiles archive" };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        var bbox = request.Bbox is { Length: 4 }
            ? request.Bbox
            : [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

        var minZoom = Math.Clamp(request.MinZoom ?? _tileLimits.MinTileZoom, _tileLimits.MinTileZoom, _tileLimits.MaxTileZoom);
        var maxZoom = Math.Clamp(request.MaxZoom ?? minZoom, minZoom, _tileLimits.MaxTileZoom);

        var minLon = Math.Min(bbox[0], bbox[2]);
        var minLat = Math.Min(bbox[1], bbox[3]);
        var maxLon = Math.Max(bbox[0], bbox[2]);
        var maxLat = Math.Max(bbox[1], bbox[3]);

        var metadata = new PMTilesArchiveMetadata
        {
            MinLon = minLon,
            MinLat = minLat,
            MaxLon = maxLon,
            MaxLat = maxLat,
            MinZoom = minZoom,
            MaxZoom = maxZoom
        };

        // Spill the archive to a self-deleting temp file rather than a MemoryStream: archive size
        // scales with bbox/zoom range and holding it on the managed heap for the upload duration is
        // an OOM/LOH pressure source. DeleteOnClose cleans up when the consumer disposes the stream.
        var archiveStream = new FileStream(
            Path.Combine(Path.GetTempPath(), $"honua-pmtiles-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var archiveSize = await writer.WriteAsync(archiveStream, metadata, cancellationToken).ConfigureAwait(false);
            archiveStream.Position = 0;

            return new PMTilesBuildResult
            {
                Progress = current with
                {
                    ProcessedTiles = processed,
                    SuccessfulTiles = successful,
                    FailedTiles = failed,
                    Warnings = warningList.ToArray()
                },
                ArchiveStream = archiveStream,
                ArchiveSize = archiveSize,
                Failed = failed,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                MinLon = minLon,
                MinLat = minLat,
                MaxLon = maxLon,
                MaxLat = maxLat
            };
        }
        catch
        {
            await archiveStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryDeletePublishArtifactAsync(
        ICloudFileStorage cloudStorage,
        string artifactId,
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await cloudStorage.DeleteAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                TileOperationLog.PublishOrphanCleanupReturnedFalse(_logger, jobId, artifactId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TileOperationLog.PublishOrphanCleanupFailed(_logger, jobId, artifactId, ex);
        }
    }

    private static string BuildPublishObjectKey(
        string? providerKeyPrefix,
        string publishKeyPrefix,
        string? serviceId,
        int layerId,
        string? tileMatrixSetId)
    {
        var serviceSegment = string.IsNullOrWhiteSpace(serviceId)
            ? "_"
            : SanitizeKeySegment(serviceId);
        var matrixSegment = string.IsNullOrWhiteSpace(tileMatrixSetId)
            ? "WebMercatorQuad"
            : SanitizeKeySegment(tileMatrixSetId);

        var segments = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(providerKeyPrefix))
        {
            segments.Add(providerKeyPrefix.Trim('/'));
        }

        if (!string.IsNullOrWhiteSpace(publishKeyPrefix))
        {
            segments.Add(publishKeyPrefix.Trim('/'));
        }

        segments.Add(serviceSegment);
        segments.Add(layerId.ToString(CultureInfo.InvariantCulture));
        segments.Add($"{matrixSegment}.pmtiles");

        return string.Join('/', segments.Where(static segment => segment.Length > 0));
    }

    private static string SanitizeKeySegment(string value)
    {
        var trimmed = value.Trim().Trim('/');
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string ResolvePublishBucket(CloudStorageOptions options)
    {
        return options.Provider switch
        {
            CloudStorageProvider.AwsS3 => options.AwsS3?.BucketName ?? string.Empty,
            CloudStorageProvider.AzureBlob => options.AzureBlob?.ContainerName ?? string.Empty,
            CloudStorageProvider.Local => options.LocalStorage?.BasePath ?? string.Empty,
            _ => string.Empty
        };
    }

    private static async Task<(string AccessUrl, DateTimeOffset? ExpiresAt)> ResolvePublishAccessUrlAsync(
        ICloudFileStorage cloudStorage,
        PMTilesPublishOptions publishOptions,
        string artifactId,
        CancellationToken cancellationToken)
    {
        switch (publishOptions.UrlStrategy)
        {
            case PMTilesUrlStrategy.PublicUrl:
                if (string.IsNullOrWhiteSpace(publishOptions.PublicBucketBaseUrl))
                {
                    throw new InvalidOperationException(
                        "PMTilesPublish:PublicBucketBaseUrl must be configured when UrlStrategy is PublicUrl.");
                }

                var publicBase = publishOptions.PublicBucketBaseUrl.TrimEnd('/');
                return ($"{publicBase}/{artifactId}", null);

            case PMTilesUrlStrategy.RangeProxy:
                return ($"/api/v1/tiles/pmtiles/{artifactId}", null);

            case PMTilesUrlStrategy.SignedUrl:
            default:
                var lifetime = publishOptions.SignedUrlLifetime <= TimeSpan.Zero
                    ? TimeSpan.FromDays(7)
                    : publishOptions.SignedUrlLifetime;
                var signedUrl = await cloudStorage.GetPresignedUrlAsync(artifactId, lifetime, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(signedUrl))
                {
                    throw new InvalidOperationException("Storage provider did not return a presigned URL for the published artifact.");
                }

                return (signedUrl, DateTimeOffset.UtcNow.Add(lifetime));
        }
    }

    private static async Task<IReadOnlyList<int>> ResolveLayerIdsAsync(
        TileOperationStartRequest request,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        if (request.LayerId.HasValue)
        {
            return [request.LayerId.Value];
        }

        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            return [];
        }

        return await ResolveServiceLayerIdsAsync(graphProvider, request.ServiceId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int[]> ResolveServiceLayerIdsAsync(
        IMetadataV2GraphProvider graphProvider,
        string serviceId,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshot.FindService(serviceId);
        if (service is null)
        {
            return [];
        }

        return snapshot.PublicationsForService(service.Metadata.Id)
            .Where(static p => p.LayerIndex.HasValue)
            .Select(static p => p.LayerIndex!.Value)
            .Distinct()
            .ToArray();
    }

    private List<TileCoordinate> BuildTileCoordinates(TileOperationStartRequest request)
    {
        var maxTiles = Math.Clamp(request.MaxTiles ?? 500, 1, _maxTilesCeiling);
        var minZoom = Math.Clamp(request.MinZoom ?? _tileLimits.MinTileZoom, _tileLimits.MinTileZoom, _tileLimits.MaxTileZoom);
        var maxZoom = Math.Clamp(request.MaxZoom ?? minZoom, minZoom, _tileLimits.MaxTileZoom);

        var bbox = request.Bbox is { Length: 4 }
            ? request.Bbox
            : [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

        var minLon = Math.Min(bbox[0], bbox[2]);
        var maxLon = Math.Max(bbox[0], bbox[2]);
        var minLat = Math.Min(bbox[1], bbox[3]);
        var maxLat = Math.Max(bbox[1], bbox[3]);

        var result = new List<TileCoordinate>(Math.Min(maxTiles, 1024));
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var n = 1 << z;
            var xMin = LonToTileX(minLon, z, n);
            var xMax = LonToTileX(maxLon, z, n);
            var yMin = LatToTileY(maxLat, z, n);
            var yMax = LatToTileY(minLat, z, n);

            for (var x = xMin; x <= xMax; x++)
            {
                for (var y = yMin; y <= yMax; y++)
                {
                    result.Add(new TileCoordinate(z, x, y));
                    if (result.Count >= maxTiles)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private static int LonToTileX(double lon, int z, int n)
    {
        var clampedLon = Math.Clamp(lon, -180d, 180d);
        var x = (int)Math.Floor((clampedLon + 180d) / 360d * n);
        return Math.Clamp(x, 0, n - 1);
    }

    private static int LatToTileY(double lat, int z, int n)
    {
        var clampedLat = Math.Clamp(lat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude);
        var latRad = clampedLat * Math.PI / 180d;
        var y = (int)Math.Floor(
            (1d - Math.Log(Math.Tan(latRad) + (1d / Math.Cos(latRad))) / Math.PI) / 2d * n);
        return Math.Clamp(y, 0, n - 1);
    }

    private readonly record struct TileCoordinate(int Z, int X, int Y);

    private sealed record PMTilesBuildResult
    {
        public required TileOperationProgress Progress { get; init; }
        public Stream? ArchiveStream { get; init; }
        public long ArchiveSize { get; init; }
        public long Failed { get; init; }
        public int MinZoom { get; init; }
        public int MaxZoom { get; init; }
        public double MinLon { get; init; }
        public double MinLat { get; init; }
        public double MaxLon { get; init; }
        public double MaxLat { get; init; }

        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(ArchiveStream))]
        public bool HasArchive => ArchiveStream is not null;

        public static PMTilesBuildResult Empty(TileOperationProgress progress) => new() { Progress = progress };
    }
}
