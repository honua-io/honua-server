// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

public readonly record struct TileCacheEvictionResult(
    int Scanned,
    int Evicted,
    bool Enabled,
    bool IndexAvailable = true);

internal sealed partial class TileCacheEvictionService(
    ITileCacheKeyIndex keyIndex,
    IOptions<Honua.Core.Features.Tiles.TileOptions> tileOptions,
    ILogger<TileCacheEvictionService> logger,
    ICloudFileStorage? storage = null)
{
    private const int EvictionBatchSize = 1_000;

    private readonly ITileCacheKeyIndex _keyIndex = keyIndex ?? throw new ArgumentNullException(nameof(keyIndex));
    private readonly TileCacheEvictionOptions _options =
        (tileOptions ?? throw new ArgumentNullException(nameof(tileOptions))).Value.Eviction;
    private readonly ILogger<TileCacheEvictionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ICloudFileStorage? _storage = storage;
    private readonly ITileCacheMutationCoordinator? _mutationCoordinator = keyIndex as ITileCacheMutationCoordinator;

    public bool IsEnabled => _options.Enabled && _keyIndex.IsEnabled;

    public async Task<TileCacheEvictionResult> SweepAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return new TileCacheEvictionResult(0, 0, Enabled: false);
        }

        long totalEntries = 0;
        long totalBytes = 0;
        var lruComparer = Comparer<TileCacheEntry>.Create(static (left, right) =>
        {
            var byAccess = left.LastAccessUtc.CompareTo(right.LastAccessUtc);
            return byAccess != 0 ? byAccess : string.CompareOrdinal(left.Key, right.Key);
        });
        var oldestCandidates = new SortedSet<TileCacheEntry>(lruComparer);
        await foreach (var page in _keyIndex.ReadPagesAsync(EvictionBatchSize, cancellationToken).ConfigureAwait(false))
        {
            if (!page.IsAvailable)
            {
                // Fail closed and say so: evict nothing, and report the index fault distinctly
                // from a deliberately disabled evictor.
                return new TileCacheEvictionResult(0, 0, Enabled: true, IndexAvailable: false);
            }

            totalEntries += page.Entries.Count;
            foreach (var entry in page.Entries)
            {
                totalBytes += entry.SizeBytes;
                _ = oldestCandidates.Add(entry);
                if (oldestCandidates.Count > EvictionBatchSize)
                {
                    _ = oldestCandidates.Remove(oldestCandidates.Max);
                }
            }
        }

        var scanned = (int)Math.Min(int.MaxValue, totalEntries);
        if (!ExceedsQuota(totalEntries, totalBytes))
        {
            return new TileCacheEvictionResult(scanned, 0, Enabled: true);
        }

        // Stable membership pages need not be LRU-ordered. The scan retained only the bounded
        // oldest candidate set, from which this sweep selects at most one victim batch. Hosted
        // sweeps converge large overages without materializing the complete index.
        var projectedEntries = totalEntries;
        var projectedBytes = totalBytes;
        var victims = new List<TileCacheEntry>(EvictionBatchSize);
        foreach (var entry in oldestCandidates)
        {
            if (!ExceedsQuota(projectedEntries, projectedBytes) || victims.Count >= EvictionBatchSize)
            {
                break;
            }

            victims.Add(entry);
            projectedEntries--;
            projectedBytes -= entry.SizeBytes;
        }

        var evicted = 0;
        foreach (var victim in victims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = victim.Key;

            var removed = false;
            if (_storage is null)
            {
                removed = await _keyIndex.TryRemoveAsync(victim, cancellationToken).ConfigureAwait(false);
            }
            else if (_mutationCoordinator is not null)
            {
                try
                {
                    await _mutationCoordinator.ExecuteSerializedAsync(
                        key,
                        async mutationContext =>
                        {
                            var mutationToken = mutationContext.CancellationToken;
                            if (!await _mutationCoordinator.IsCurrentAsync(victim, mutationToken).ConfigureAwait(false))
                            {
                                return;
                            }

                            // Hold the same per-key fence as hot cache writes across both the
                            // irreversible storage delete and the conditional index removal.
                            if (!await TileCacheStorageDeletion
                                    .DeleteCurrentOrConfirmMissingAsync(_storage, key, mutationToken)
                                    .ConfigureAwait(false))
                            {
                                return;
                            }

                            removed = await _keyIndex.TryRemoveAsync(victim, mutationToken).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.EvictionDeleteFailed(_logger, ex);
                }
            }

            if (!removed)
            {
                continue;
            }
            evicted++;
        }

        Log.EvictionSweepCompleted(_logger, scanned, evicted);
        return new TileCacheEvictionResult(scanned, evicted, Enabled: true);
    }

    private bool ExceedsQuota(long totalEntries, long totalBytes)
        => (_options.MaxEntries is { } maxEntries && maxEntries > 0 && totalEntries > maxEntries) ||
           (_options.MaxBytes is { } maxBytes && maxBytes > 0 && totalBytes > maxBytes);

    private static partial class Log
    {
        [LoggerMessage(EventId = 9263, Level = LogLevel.Information, Message = "Tile-cache eviction sweep scanned {Scanned} entries and evicted {Evicted} least-recently-used tiles.")]
        public static partial void EvictionSweepCompleted(ILogger logger, int scanned, int evicted);

        [LoggerMessage(EventId = 9264, Level = LogLevel.Warning, Message = "Failed to delete an evicted tile from the cache store; it remains tracked for the next sweep.")]
        public static partial void EvictionDeleteFailed(ILogger logger, Exception exception);
    }
}

internal static class TileCacheStorageDeletion
{
    public static async Task<bool> DeleteCurrentOrConfirmMissingAsync(
        ICloudFileStorage storage,
        string key,
        CancellationToken cancellationToken)
    {
        var target = await storage.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return true;
        }

        // Lifecycle and quota sweeps act on an index snapshot. Capture the exact storage
        // generation while the per-key mutation fence is held and make the destructive call
        // conditional, so a stale sweep can never remove replacement bytes at the same key.
        if (string.IsNullOrWhiteSpace(target.ETag))
        {
            return false;
        }

        if (await storage.DeleteIfMatchAsync(key, target.ETag, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await storage.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false) is null;
    }
}
