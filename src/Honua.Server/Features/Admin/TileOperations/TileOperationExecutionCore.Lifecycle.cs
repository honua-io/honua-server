// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Bounded generated tile-cache lifecycle operations (issue #2661): <c>expire</c> marks a bounded
/// key window stale without deleting bytes, while <c>delete</c> removes the bytes outright. Both act
/// only on the generated (loose <c>{z}/{x}/{y}</c>) tile objects the live
/// <see cref="ITileCacheKeyIndex"/> tracks, deriving the target key window from the SAME
/// <c>(service, gridset, z-range, extent, style, format)</c> inputs the seed/serve path uses so the
/// window can never over- or under-reach the intended tiles.
/// </summary>
internal sealed partial class TileOperationExecutionCore
{
    private async Task<TileOperationProgress> ExecuteExpireOrDeleteAsync(
        TileOperationProgress progress,
        TileOperationStartRequest request,
        bool deleteBytes,
        IMetadataV2GraphProvider graphProvider,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TileMatrixSetId, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only TileMatrixSetId 'WebMercatorQuad' is currently supported.");
        }

        // Expire keeps the HTTP-layer invalidation the invalidate/purge operations perform so
        // cached responses at the edge are dropped alongside the storage-layer staleness marking.
        if (!deleteBytes)
        {
            await InvalidateHttpLayerAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        }

        var keyIndex = serviceProvider.GetService<ITileCacheKeyIndex>();
        if (keyIndex is not { IsEnabled: true })
        {
            // No live index means no generated tiles are tracked to act on. This is a clean no-op
            // (expire still invalidated the HTTP layer above); report zero work rather than failing.
            TileOperationLog.LifecycleIndexUnavailable(_logger, request.Operation);
            return progress with
            {
                Status = OperationStatus.Completed,
                TotalTiles = 0,
                ProcessedTiles = 0,
                SuccessfulTiles = 0,
                FailedTiles = 0,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = $"{request.Operation} completed (no tracked tiles)"
            };
        }

        var storage = serviceProvider.GetService<ICloudFileStorage>();
        if (deleteBytes && storage is null)
        {
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Cloud storage is not configured. Delete operations require cloud storage.",
                CurrentPhase = "Failed"
            };
        }

        var targetLayers = await ResolveLayerIdsAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        var window = TileCacheKeyWindow.Create(request, targetLayers, _tileLimits);

        var snapshot = await keyIndex.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var matched = new List<TileCacheEntry>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            if (window.Matches(entry.Key))
            {
                matched.Add(entry);
            }
        }

        // Deterministic ordinal order so a resumed attempt processes the window in the same order.
        matched.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var generationId = string.IsNullOrWhiteSpace(request.GenerationId) ? null : request.GenerationId;
        var checkpointEnabled = _checkpointStore is not null && generationId is not null;
        var checkpoint = checkpointEnabled
            ? await LoadCheckpointBestEffortAsync(generationId!, cancellationToken).ConfigureAwait(false)
            : null;
        var attempt = (checkpoint?.Attempt ?? 0) + 1;
        if (checkpoint is not null)
        {
            TileOperationLog.GenerationResumed(_logger, generationId!, checkpoint.CompletedMetatileBlocks, checkpoint.FailedUnits.Count, attempt);
        }

        var phase = deleteBytes ? "Deleting tiles" : "Expiring tiles";
        var total = (long)matched.Count;
        var affected = checkpoint?.CompletedUnitCount ?? 0L;
        var processed = 0L;
        var failed = 0L;
        var bytesReleased = 0L;
        var newFailedUnits = new HashSet<string>(StringComparer.Ordinal);

        var current = progress with
        {
            TotalTiles = total,
            SuccessfulTiles = affected,
            CurrentPhase = phase
        };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < matched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = matched[i];

            var release = true;
            if (deleteBytes)
            {
                try
                {
                    // Storage-first ordering (mirrors TileCacheEvictionService.SweepAsync): only drop
                    // the index entry once the byte is gone, so a failed delete leaves the key tracked
                    // for a retry rather than orphaning a tile in cloud storage.
                    await storage!.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TileOperationLog.LifecycleDeleteFailed(_logger, ex);
                    failed++;
                    newFailedUnits.Add(TruncateUnit(entry.Key));
                    release = false;
                }
            }

            if (release)
            {
                // Delete: index drop follows the successful storage delete. Expire: drop the index
                // entry so the tile stops counting against the LRU quota and its natural object TTL
                // regenerates it on the next request (bytes are intentionally retained).
                await keyIndex.RemoveAsync(entry.Key, cancellationToken).ConfigureAwait(false);
                affected++;
                bytesReleased += entry.SizeBytes;
            }

            processed++;
            if (processed % 25 == 0 || processed == total)
            {
                current = current with
                {
                    ProcessedTiles = affected + failed,
                    SuccessfulTiles = affected,
                    FailedTiles = failed,
                    ArchiveSizeBytes = bytesReleased,
                    CurrentPhase = $"{phase} ({processed}/{total})"
                };
                await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

                if (checkpointEnabled)
                {
                    await PersistCheckpointBestEffortAsync(
                        generationId!,
                        request.Operation,
                        i + 1,
                        affected,
                        failed,
                        newFailedUnits,
                        attempt,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (affected > 0)
        {
            var counter = deleteBytes ? TileOperationMetrics.TilesDeleted : TileOperationMetrics.TilesExpired;
            counter.Add(affected, new TagList { { "operation", request.Operation } });
            TileOperationMetrics.CacheBytesReleased.Add(bytesReleased, new TagList { { "operation", request.Operation } });
        }

        TileOperationLog.LifecycleWindowCompleted(_logger, request.Operation, matched.Count, affected);

        current = current with
        {
            ProcessedTiles = affected + failed,
            SuccessfulTiles = affected,
            FailedTiles = failed,
            ArchiveSizeBytes = bytesReleased
        };

        if (failed == 0)
        {
            if (checkpointEnabled)
            {
                await DeleteCheckpointBestEffortAsync(generationId!, cancellationToken).ConfigureAwait(false);
            }

            return current with
            {
                Status = OperationStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null,
                CurrentPhase = $"{request.Operation} completed"
            };
        }

        // Failed deletes leave the checkpoint in place so a fix-forward retry re-snapshots the index
        // (which still tracks the failed keys) and reprocesses only what remains under the same id.
        return current with
        {
            Status = OperationStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = $"{failed} tiles failed to delete from the cache store.",
            CurrentPhase = $"{request.Operation} completed with failures"
        };
    }

    private async Task InvalidateHttpLayerAsync(
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
    }

    private static string TruncateUnit(string key)
        => key.Length > TileCacheGenerationCheckpointBounds.MaxFailedUnitLength
            ? key[..TileCacheGenerationCheckpointBounds.MaxFailedUnitLength]
            : key;

    /// <summary>
    /// The bounded target window for a generated-tile-cache expire/delete run. Encapsulates the
    /// single object-key convention the generated tile cache uses so the delete window is derived in
    /// exactly one place: a key is matched when its parsed <c>(layerId, gridset, style, format, z, x,
    /// y)</c> falls inside the requested <c>(service→layers, gridset, style, format, z-range,
    /// extent)</c> bound. Keys that do not parse as generated tiles never match.
    /// </summary>
    private readonly struct TileCacheKeyWindow
    {
        private readonly HashSet<int> _layers;
        private readonly bool _anyLayer;
        private readonly string _gridset;
        private readonly string _style;
        private readonly string? _format;
        private readonly int _minZoom;
        private readonly int _maxZoom;
        private readonly double _minLon;
        private readonly double _minLat;
        private readonly double _maxLon;
        private readonly double _maxLat;
        private readonly bool _hasBbox;

        private TileCacheKeyWindow(
            HashSet<int> layers,
            bool anyLayer,
            string gridset,
            string style,
            string? format,
            int minZoom,
            int maxZoom,
            double minLon,
            double minLat,
            double maxLon,
            double maxLat,
            bool hasBbox)
        {
            _layers = layers;
            _anyLayer = anyLayer;
            _gridset = gridset;
            _style = style;
            _format = format;
            _minZoom = minZoom;
            _maxZoom = maxZoom;
            _minLon = minLon;
            _minLat = minLat;
            _maxLon = maxLon;
            _maxLat = maxLat;
            _hasBbox = hasBbox;
        }

        public static TileCacheKeyWindow Create(
            TileOperationStartRequest request,
            IReadOnlyList<int> targetLayers,
            TileLimits tileLimits)
        {
            // No resolvable layer (no layerId and no service publications) means the window applies
            // to every tracked layer; otherwise it is scoped to the resolved layer set.
            var layers = new HashSet<int>(targetLayers);
            var anyLayer = layers.Count == 0;

            var gridset = GeneratedTileCacheKey.Sanitize(string.IsNullOrWhiteSpace(request.TileMatrixSetId) ? "WebMercatorQuad" : request.TileMatrixSetId);
            var style = GeneratedTileCacheKey.Sanitize(string.IsNullOrWhiteSpace(request.Style) ? "default" : request.Style);
            var format = string.IsNullOrWhiteSpace(request.Format) ? null : GeneratedTileCacheKey.Sanitize(request.Format);

            // Unlike seeding (which defaults maxZoom to minZoom to produce a single level), a bounded
            // delete/expire defaults to the full supported zoom range so an operator who scopes only
            // by layer/style removes the whole pyramid rather than one level.
            var minZoom = Math.Clamp(request.MinZoom ?? tileLimits.MinTileZoom, tileLimits.MinTileZoom, tileLimits.MaxTileZoom);
            var maxZoom = Math.Clamp(request.MaxZoom ?? tileLimits.MaxTileZoom, minZoom, tileLimits.MaxTileZoom);

            var hasBbox = request.Bbox is { Length: 4 };
            var bbox = hasBbox
                ? request.Bbox!
                : [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

            return new TileCacheKeyWindow(
                layers,
                anyLayer,
                gridset,
                style,
                format,
                minZoom,
                maxZoom,
                Math.Min(bbox[0], bbox[2]),
                Math.Min(bbox[1], bbox[3]),
                Math.Max(bbox[0], bbox[2]),
                Math.Max(bbox[1], bbox[3]),
                hasBbox);
        }

        public bool Matches(string key)
        {
            if (!GeneratedTileCacheKey.TryParse(key, out var parsed))
            {
                return false;
            }

            if (!_anyLayer && !_layers.Contains(parsed.LayerId))
            {
                return false;
            }

            if (!string.Equals(parsed.Gridset, _gridset, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(parsed.Style, _style, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_format is not null && !string.Equals(parsed.Format, _format, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var z = parsed.Z;
            if (z < _minZoom || z > _maxZoom)
            {
                return false;
            }

            if (!_hasBbox)
            {
                return true;
            }

            var n = 1 << z;
            var xMin = LonToTileX(_minLon, z, n);
            var xMax = LonToTileX(_maxLon, z, n);
            var yMin = LatToTileY(_maxLat, z, n);
            var yMax = LatToTileY(_minLat, z, n);
            return parsed.X >= xMin && parsed.X <= xMax && parsed.Y >= yMin && parsed.Y <= yMax;
        }
    }
}
