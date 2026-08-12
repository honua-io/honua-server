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
        string? tenantScope,
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
            TileOperationLog.LifecycleIndexUnavailable(_logger, request.Operation);
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "The tile cache index is not configured; no cache entries were changed.",
                CurrentPhase = "Failed"
            };
        }

        var mutationCoordinator = keyIndex as ITileCacheMutationCoordinator;
        if (mutationCoordinator is null)
        {
            return progress with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "The tile cache index does not support fenced lifecycle mutations; no cache entries were changed.",
                CurrentPhase = "Failed"
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

        var targetLayers = await TileCacheTargetResolver.ResolveLayerIdsAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        tenantScope ??= TileCacheTenantScope.Resolve(serviceProvider);
        var window = TileCacheKeyWindow.Create(request, targetLayers, _tileLimits, tenantScope);

        var maxTiles = Math.Clamp(request.MaxTiles ?? _maxTilesCeiling, 1, _maxTilesCeiling);
        var generationId = string.IsNullOrWhiteSpace(request.GenerationId) ? null : request.GenerationId;
        var checkpointEnabled = _checkpointStore is not null && generationId is not null;
        var checkpoint = checkpointEnabled
            ? deleteBytes
                ? await _checkpointStore!.LoadAsync(generationId!, cancellationToken).ConfigureAwait(false)
                : await LoadCheckpointBestEffortAsync(generationId!, cancellationToken).ConfigureAwait(false)
            : null;
        var attempt = (checkpoint?.Attempt ?? 0) + 1;
        if (checkpoint is not null)
        {
            TileOperationLog.GenerationResumed(_logger, generationId!, checkpoint.CompletedMetatileBlocks, checkpoint.FailedUnits.Count, attempt);
        }

        // Delete reservations, including a key whose prior mutation failed, consume the original
        // generation safety budget. FailedUnits is also the durable set of reserved keys that a
        // retry may process without consuming a second slot. This selection admits those pending
        // keys first and only then fills the still-unreserved portion of MaxTiles.
        var deleteReservations = deleteBytes
            ? Math.Min(maxTiles, checkpoint?.CompletedUnitCount ?? 0L)
            : 0L;
        var pendingDeleteUnits = deleteBytes && checkpoint is { FailedUnits.Count: > 0 }
            ? new HashSet<string>(checkpoint.FailedUnits, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var remainingDeleteBudget = deleteBytes
            ? (int)Math.Max(0L, maxTiles - deleteReservations)
            : maxTiles;
        var entryComparer = Comparer<TileCacheEntry>.Create(
            static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        var pendingCandidates = new SortedSet<TileCacheEntry>(entryComparer);
        var freshCandidates = new SortedSet<TileCacheEntry>(entryComparer);
        var matchedCount = 0L;

        await foreach (var page in keyIndex.ReadPagesAsync(1_000, cancellationToken).ConfigureAwait(false))
        {
            if (!page.IsAvailable)
            {
                return progress with
                {
                    Status = OperationStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "The tile cache index is temporarily unavailable; no cache entries were changed.",
                    CurrentPhase = "Failed"
                };
            }

            foreach (var entry in page.Entries)
            {
                if (!window.Matches(entry))
                {
                    continue;
                }

                matchedCount++;
                if (deleteBytes && pendingDeleteUnits.Contains(TruncateUnit(entry.Key)))
                {
                    _ = pendingCandidates.Add(entry);
                    continue;
                }

                AddBoundedLifecycleCandidate(freshCandidates, entry, remainingDeleteBudget);
            }
        }

        pendingDeleteUnits.IntersectWith(
            pendingCandidates.Select(static entry => TruncateUnit(entry.Key)));
        var matched = pendingCandidates
            .Concat(freshCandidates)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToList();
        var warnings = BuildLifecycleWarnings(
            matchedCount,
            matched.Count,
            maxTiles,
            deleteBytes,
            deleteReservations,
            remainingDeleteBudget);

        var phase = deleteBytes ? "Deleting tiles" : "Expiring tiles";
        var total = (long)matched.Count;
        // The snapshot is the complete accounting window for this attempt. Delete retries no
        // longer contain successfully removed keys, while expire retries still contain already
        // expired keys; carrying the prior checkpoint count into either snapshot would make the
        // reported and metered success count exceed TotalTiles.
        var affected = 0L;
        var processed = 0L;
        var failed = 0L;
        var bytesReleased = 0L;
        var mutations = 0L;
        var excluded = 0L;
        var newFailedUnits = deleteBytes
            ? new HashSet<string>(pendingDeleteUnits, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var current = progress with
        {
            TotalTiles = total,
            SuccessfulTiles = affected,
            Warnings = warnings,
            CurrentPhase = phase
        };
        await _progressStore.SetProgressAsync(current.JobId, current, ProgressRetention, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < matched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = matched[i];
            var unit = TruncateUnit(entry.Key);

            if (deleteBytes && checkpointEnabled && !newFailedUnits.Contains(unit))
            {
                if (newFailedUnits.Count >= TileCacheGenerationCheckpointBounds.MaxFailedUnits)
                {
                    throw new InvalidOperationException(
                        "The lifecycle delete checkpoint cannot reserve another key without exceeding its durable bound.");
                }

                // Reserve the safety-cap slot before the irreversible storage delete. SaveAsync is
                // intentionally not best-effort here: if durable accounting is unavailable, no
                // bytes for this key may be changed.
                newFailedUnits.Add(unit);
                deleteReservations++;
                await PersistLifecycleDeleteCheckpointAsync(
                    generationId!,
                    request.Operation,
                    i,
                    deleteReservations,
                    newFailedUnits.Count,
                    newFailedUnits,
                    attempt).ConfigureAwait(false);
            }

            var release = true;
            try
            {
                if (deleteBytes)
                {
                    await mutationCoordinator!.ExecuteSerializedAsync(
                        entry.Key,
                        async mutationContext =>
                        {
                            var mutationToken = mutationContext.CancellationToken;
                            if (!await mutationCoordinator.IsCurrentAsync(entry, mutationToken).ConfigureAwait(false))
                            {
                                throw new InvalidOperationException(
                                    "The tile cache entry was replaced by a concurrent write before deletion.");
                            }

                            if (!await TileCacheStorageDeletion
                                    .DeleteOrConfirmMissingAsync(storage!, entry.Key, mutationToken)
                                    .ConfigureAwait(false))
                            {
                                throw new InvalidOperationException(
                                    "The tile remained in cloud storage after the delete attempt.");
                            }

                            if (!await keyIndex.TryRemoveAsync(entry, mutationToken).ConfigureAwait(false))
                            {
                                throw new InvalidOperationException(
                                    "The tile cache entry changed while deletion held its mutation fence.");
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Keep the bytes and quota entry, but make the hot read path treat the object as
                    // a miss. Set-add is idempotent, so always write the marker: an expiration-read
                    // failure must not be mistaken for prior success. Holding the same per-key fence
                    // as RecordWriteAsync orders the marker against concurrent regeneration.
                    var markResult = TileCacheExpirationMarkResult.NotCurrent;
                    await mutationCoordinator!.ExecuteSerializedAsync(
                        entry.Key,
                        async mutationContext =>
                        {
                            var mutationToken = mutationContext.CancellationToken;
                            markResult = await mutationCoordinator
                                .TryMarkExpiredIfCurrentAsync(entry, mutationToken)
                                .ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (markResult == TileCacheExpirationMarkResult.NotCurrent)
                    {
                        excluded++;
                        release = false;
                    }

                    if (markResult == TileCacheExpirationMarkResult.Added)
                    {
                        mutations++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TileOperationLog.LifecycleMutationFailed(_logger, request.Operation, ex);
                failed++;
                newFailedUnits.Add(unit);
                release = false;
            }

            if (release)
            {
                if (deleteBytes)
                {
                    bytesReleased += entry.SizeBytes;
                    mutations++;
                    newFailedUnits.Remove(unit);
                }

                affected++;
            }

            processed++;
            if (processed % 25 == 0 || processed == total)
            {
                current = current with
                {
                    ProcessedTiles = affected + failed,
                    TotalTiles = total - excluded,
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
                        deleteBytes ? deleteReservations : affected,
                        deleteBytes ? newFailedUnits.Count : failed,
                        newFailedUnits,
                        attempt,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (mutations > 0)
        {
            var counter = deleteBytes ? TileOperationMetrics.TilesDeleted : TileOperationMetrics.TilesExpired;
            counter.Add(mutations, new TagList { { "operation", request.Operation } });
            if (deleteBytes)
            {
                TileOperationMetrics.CacheBytesReleased.Add(bytesReleased, new TagList { { "operation", request.Operation } });
            }
        }

        TileOperationLog.LifecycleWindowCompleted(_logger, request.Operation, matched.Count, affected);

        current = current with
        {
            TotalTiles = total - excluded,
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

        // Failed mutations leave the checkpoint in place so a fix-forward retry re-snapshots the
        // index and reprocesses only what remains under the same id.
        return current with
        {
            Status = OperationStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = $"{failed} tiles failed during cache {request.Operation}.",
            CurrentPhase = $"{request.Operation} completed with failures"
        };
    }

    private async Task PersistLifecycleDeleteCheckpointAsync(
        string generationId,
        string operation,
        int completedUnits,
        long completedUnitCount,
        long failedUnitCount,
        IReadOnlyCollection<string> failedUnits,
        int attempt)
    {
        await _checkpointStore!.SaveAsync(
            new TileCacheGenerationCheckpoint
            {
                GenerationId = generationId,
                Operation = operation,
                CompletedMetatileBlocks = completedUnits,
                CompletedUnitCount = completedUnitCount,
                FailedUnitCount = failedUnitCount,
                FailedUnits = failedUnits.ToArray(),
                CapturedAt = DateTimeOffset.UtcNow,
                Attempt = attempt
            },
            CancellationToken.None).ConfigureAwait(false);
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

    private static void AddBoundedLifecycleCandidate(
        SortedSet<TileCacheEntry> candidates,
        TileCacheEntry entry,
        int limit)
    {
        if (limit < 1 || !candidates.Add(entry) || candidates.Count <= limit)
        {
            return;
        }

        _ = candidates.Remove(candidates.Max);
    }

    private static string[] BuildLifecycleWarnings(
        long matchedCount,
        int selectedCount,
        int maxTiles,
        bool deleteBytes,
        long deleteReservations,
        int remainingDeleteBudget)
    {
        var warnings = new List<string>(2);
        if (matchedCount > maxTiles)
        {
            warnings.Add(
                $"The cache lifecycle window matched {matchedCount} tiles and was truncated to the {maxTiles}-tile safety cap.");
        }

        if (deleteBytes && selectedCount < matchedCount && deleteReservations > 0)
        {
            warnings.Add(
                $"The retry was limited to the {remainingDeleteBudget}-tile budget remaining under the original {maxTiles}-tile safety cap.");
        }

        return warnings.ToArray();
    }

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
        private readonly string? _tenantScope;

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
            bool hasBbox,
            string? tenantScope)
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
            _tenantScope = tenantScope;
        }

        public static TileCacheKeyWindow Create(
            TileOperationStartRequest request,
            IReadOnlyList<int> targetLayers,
            TileLimits tileLimits,
            string? tenantScope)
        {
            // An unscoped internal request may target every layer. A named service that does not
            // resolve must instead match nothing; treating its empty layer set as "all layers"
            // would turn a misspelled service id into a deployment-wide lifecycle operation.
            var layers = new HashSet<int>(targetLayers);
            var anyLayer = layers.Count == 0
                && string.IsNullOrWhiteSpace(request.ServiceId)
                && !request.LayerId.HasValue;

            var gridset = GeneratedTileCacheKey.Sanitize(string.IsNullOrWhiteSpace(request.TileMatrixSetId) ? "WebMercatorQuad" : request.TileMatrixSetId);
            var style = GeneratedTileCacheKey.Sanitize(string.IsNullOrWhiteSpace(request.Style) ? "default" : request.Style);
            var format = string.IsNullOrWhiteSpace(request.Format) ? null : NormalizeFormat(request.Format);

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
                hasBbox,
                tenantScope);
        }

        private static string NormalizeFormat(string format)
        {
            var sanitized = GeneratedTileCacheKey.Sanitize(format);
            return sanitized switch
            {
                "jpeg" => "jpg",
                "tiff" or "cog" => "tif",
                _ => sanitized
            };
        }

        public bool Matches(TileCacheEntry entry)
        {
            if (!string.Equals(entry.TenantScope, _tenantScope, StringComparison.Ordinal)
                || !GeneratedTileCacheKey.TryParse(entry.Key, out var parsed))
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
