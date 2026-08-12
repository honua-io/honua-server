// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// The result of a single tile-cache eviction sweep (#1917).
/// </summary>
/// <param name="Scanned">The number of cache entries the index held at the start of the sweep.</param>
/// <param name="Evicted">The number of tiles deleted from the cache store and index.</param>
/// <param name="Enabled">Whether eviction was enabled and a live index was available for this sweep.</param>
public readonly record struct TileCacheEvictionResult(int Scanned, int Evicted, bool Enabled);

/// <summary>
/// Drives a single size-quota / LRU eviction pass over the live Redis tile-key index (#1917).
/// It snapshots the index, asks <see cref="TileCacheQuotaPolicy" /> which least-recently-used keys to
/// drop to satisfy the configured <see cref="TileCacheEvictionOptions" />, then deletes each victim
/// from the cloud tile store and from the index. This is the live binding that replaces relying
/// solely on the Redis server <c>maxmemory-policy</c>; the policy here honors Honua's per-deployment
/// entry-count and byte-size quotas. The class is registered as a singleton so both the scheduled
/// <see cref="TileCacheEvictionHostedService" /> and the on-demand admin endpoint reuse it.
/// </summary>
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

    /// <summary>Whether eviction is enabled and a live index is available.</summary>
    public bool IsEnabled => _options.Enabled && _keyIndex.IsEnabled;

    /// <summary>
    /// Runs one eviction sweep. Returns the entry count scanned and the number evicted. When eviction
    /// is disabled or no live index is present the sweep is a no-op with <c>Enabled = false</c>.
    /// </summary>
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
                return new TileCacheEvictionResult(0, 0, Enabled: false);
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
                                    .DeleteOrConfirmMissingAsync(_storage, key, mutationToken)
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

/// <summary>
/// Storage-first tile deletion that treats a false delete result as success only after the
/// provider independently confirms the object is absent. This keeps transient backend failures
/// indexed and retryable.
/// </summary>
internal static class TileCacheStorageDeletion
{
    public static async Task<bool> DeleteOrConfirmMissingAsync(
        ICloudFileStorage storage,
        string key,
        CancellationToken cancellationToken)
    {
        if (await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await storage.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false) is null;
    }
}
