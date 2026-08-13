// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        var target = await TileCacheTargetResolver.ResolveAsync(request, graphProvider, cancellationToken).ConfigureAwait(false);
        tenantScope ??= TileCacheTenantScope.Resolve(serviceProvider);
        var window = TileCacheKeyWindow.Create(request, target, _tileLimits, tenantScope);

        var maxTiles = Math.Clamp(request.MaxTiles ?? _maxTilesCeiling, 1, _maxTilesCeiling);
        var generationId = string.IsNullOrWhiteSpace(request.GenerationId) ? null : request.GenerationId;
        var checkpointId = generationId is null
            ? null
            : window.BuildCheckpointId(generationId, request.Operation, maxTiles);
        var checkpointEnabled = _checkpointStore is not null && checkpointId is not null;
        var checkpoint = checkpointEnabled
            ? await _checkpointStore!.LoadAsync(checkpointId!, cancellationToken).ConfigureAwait(false)
            : null;
        var attempt = (checkpoint?.Attempt ?? 0) + 1;
        if (checkpoint is not null)
        {
            TileOperationLog.GenerationResumed(_logger, generationId!, checkpoint.CompletedMetatileBlocks, checkpoint.FailedUnits.Count, attempt);
        }

        // Lifecycle reservations, including a key whose prior mutation failed, consume the
        // original generation safety budget. FailedUnits is also the durable set of reserved keys
        // that a retry may process without consuming a second slot. This selection admits those
        // pending keys first and only then fills the still-unreserved portion of MaxTiles.
        var lifecycleReservations = Math.Min(maxTiles, checkpoint?.CompletedUnitCount ?? 0L);
        var pendingLifecycleUnits = checkpoint is { FailedUnits.Count: > 0 }
            ? new HashSet<string>(checkpoint.FailedUnits, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var remainingLifecycleBudget = (int)Math.Max(0L, maxTiles - lifecycleReservations);
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

                // Expiration retains the index row and storage bytes. Exclude a marker observed in
                // the same bounded snapshot before applying MaxTiles so repeated capped jobs move
                // forward instead of selecting the same lexicographic prefix forever. Delete still
                // includes marked entries because it must remove their retained bytes.
                if (!deleteBytes && entry.IsExplicitlyExpired)
                {
                    continue;
                }

                matchedCount++;
                if (pendingLifecycleUnits.Contains(TruncateUnit(entry.Key)))
                {
                    _ = pendingCandidates.Add(entry);
                    continue;
                }

                AddBoundedLifecycleCandidate(freshCandidates, entry, remainingLifecycleBudget);
            }
        }

        pendingLifecycleUnits.IntersectWith(
            pendingCandidates.Select(static entry => TruncateUnit(entry.Key)));
        var matched = pendingCandidates
            .Concat(freshCandidates)
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToList();
        var warnings = BuildLifecycleWarnings(
            matchedCount,
            matched.Count,
            maxTiles,
            lifecycleReservations,
            remainingLifecycleBudget);

        var phase = deleteBytes ? "Deleting tiles" : "Expiring tiles";
        var total = (long)matched.Count;
        // The snapshot is the complete accounting window for this attempt. Delete retries no
        // longer contain successfully removed keys, and expire retries exclude entries whose
        // markers were present in the snapshot. Reservations remain cumulative only in the
        // checkpoint so attempt-local progress and metrics cannot exceed TotalTiles.
        var affected = 0L;
        var processed = 0L;
        var failed = 0L;
        var bytesReleased = 0L;
        var mutations = 0L;
        var excluded = 0L;
        var newFailedUnits = new HashSet<string>(pendingLifecycleUnits, StringComparer.Ordinal);

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

            if (checkpointEnabled && !newFailedUnits.Contains(unit))
            {
                if (newFailedUnits.Count >= TileCacheGenerationCheckpointBounds.MaxFailedUnits)
                {
                    throw new InvalidOperationException(
                        "The lifecycle checkpoint cannot reserve another key without exceeding its durable bound.");
                }

                // Reserve the safety-cap slot before the lifecycle mutation. SaveAsync is
                // intentionally not best-effort here: if durable accounting is unavailable, no
                // bytes or expiration state for this key may be changed.
                newFailedUnits.Add(unit);
                lifecycleReservations++;
                await PersistLifecycleCheckpointAsync(
                    checkpointId!,
                    request.Operation,
                    i,
                    lifecycleReservations,
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
                                    .DeleteCurrentOrConfirmMissingAsync(storage!, entry.Key, mutationToken)
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
                }

                newFailedUnits.Remove(unit);
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
                        checkpointId!,
                        request.Operation,
                        i + 1,
                        lifecycleReservations,
                        newFailedUnits.Count,
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
                await DeleteCheckpointBestEffortAsync(checkpointId!, cancellationToken).ConfigureAwait(false);
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

    private async Task PersistLifecycleCheckpointAsync(
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
        long lifecycleReservations,
        int remainingLifecycleBudget)
    {
        var warnings = new List<string>(2);
        if (matchedCount > maxTiles)
        {
            warnings.Add(
                $"The cache lifecycle window matched {matchedCount} tiles and was truncated to the {maxTiles}-tile safety cap.");
        }

        if (selectedCount < matchedCount && lifecycleReservations > 0)
        {
            warnings.Add(
                $"The retry was limited to the {remainingLifecycleBudget}-tile budget remaining under the original {maxTiles}-tile safety cap.");
        }

        return warnings.ToArray();
    }

    /// <summary>
    /// The bounded target window for a generated-tile-cache expire/delete run. Encapsulates the
    /// single object-key convention the generated tile cache uses so the delete window is derived in
    /// exactly one place: a key is matched when its parsed <c>(publicationScope, layerId, gridset,
    /// style, format, z, x, y)</c> falls inside the requested <c>(service→publications, gridset,
    /// style, format, z-range, extent)</c> bound. Keys that do not parse as generated tiles never
    /// match.
    /// </summary>
    private readonly struct TileCacheKeyWindow
    {
        private readonly HashSet<int> _layers;
        private readonly HashSet<string> _publicationScopes;
        private readonly bool _serviceScoped;
        private readonly bool _anyLayer;
        private readonly string _gridset;
        private readonly string _style;
        private readonly string? _format;
        private readonly int _minZoom;
        private readonly int _maxZoom;
        private readonly double _westLon;
        private readonly double _minLat;
        private readonly double _eastLon;
        private readonly double _maxLat;
        private readonly bool _hasBbox;
        private readonly string? _tenantScope;

        private TileCacheKeyWindow(
            HashSet<int> layers,
            HashSet<string> publicationScopes,
            bool serviceScoped,
            bool anyLayer,
            string gridset,
            string style,
            string? format,
            int minZoom,
            int maxZoom,
            double westLon,
            double minLat,
            double eastLon,
            double maxLat,
            bool hasBbox,
            string? tenantScope)
        {
            _layers = layers;
            _publicationScopes = publicationScopes;
            _serviceScoped = serviceScoped;
            _anyLayer = anyLayer;
            _gridset = gridset;
            _style = style;
            _format = format;
            _minZoom = minZoom;
            _maxZoom = maxZoom;
            _westLon = westLon;
            _minLat = minLat;
            _eastLon = eastLon;
            _maxLat = maxLat;
            _hasBbox = hasBbox;
            _tenantScope = tenantScope;
        }

        public static TileCacheKeyWindow Create(
            TileOperationStartRequest request,
            TileCacheTargetResolution target,
            TileLimits tileLimits,
            string? tenantScope)
        {
            // An unscoped internal request may target every layer. A named service that does not
            // resolve must instead match nothing; treating its empty layer set as "all layers"
            // would turn a misspelled service id into a deployment-wide lifecycle operation.
            var layers = new HashSet<int>(target.LayerIds);
            var publicationScopes = target.PublicationIds
                .Select(TileCachePublicationScope.Create)
                .ToHashSet(StringComparer.Ordinal);
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
                publicationScopes,
                target.IsServiceScoped,
                anyLayer,
                gridset,
                style,
                format,
                minZoom,
                maxZoom,
                bbox[0],
                Math.Min(bbox[1], bbox[3]),
                bbox[2],
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

        public string BuildCheckpointId(string generationId, string operation, int maxTiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);

            var scope = new StringBuilder(384);
            AppendScopeComponent(scope, "v2");
            AppendScopeComponent(scope, generationId.Trim());
            AppendScopeComponent(scope, operation);
            AppendScopeComponent(scope, _tenantScope);
            AppendScopeComponent(scope, _anyLayer ? "all" : "selected");
            foreach (var layerId in _layers.Order())
            {
                AppendScopeComponent(scope, layerId.ToString(CultureInfo.InvariantCulture));
            }

            AppendScopeComponent(scope, _serviceScoped ? "service" : "any-service");
            foreach (var publicationScope in _publicationScopes.Order(StringComparer.Ordinal))
            {
                AppendScopeComponent(scope, publicationScope);
            }

            AppendScopeComponent(scope, _gridset);
            AppendScopeComponent(scope, _style);
            AppendScopeComponent(scope, _format);
            AppendScopeComponent(scope, _minZoom.ToString(CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, _maxZoom.ToString(CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, _hasBbox ? "bbox" : "global");
            AppendScopeComponent(scope, _westLon.ToString("R", CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, _minLat.ToString("R", CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, _eastLon.ToString("R", CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, _maxLat.ToString("R", CultureInfo.InvariantCulture));
            AppendScopeComponent(scope, maxTiles.ToString(CultureInfo.InvariantCulture));

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToString()));
            return $"lifecycle-{Convert.ToHexString(digest).ToLowerInvariant()}";
        }

        private static void AppendScopeComponent(StringBuilder scope, string? value)
        {
            var normalized = value ?? string.Empty;
            _ = scope
                .Append(normalized.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(normalized)
                .Append('|');
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

            if (_serviceScoped
                && (parsed.PublicationScope is null || !_publicationScopes.Contains(parsed.PublicationScope)))
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
            var xWest = LonToTileX(_westLon, z, n);
            var xEast = LonToTileX(_eastLon, z, n);
            var yMin = LatToTileY(_maxLat, z, n);
            var yMax = LatToTileY(_minLat, z, n);
            var xMatches = _westLon <= _eastLon
                ? parsed.X >= xWest && parsed.X <= xEast
                : parsed.X >= xWest || parsed.X <= xEast;
            return xMatches && parsed.Y >= yMin && parsed.Y <= yMax;
        }
    }
}
