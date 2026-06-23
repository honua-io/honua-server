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
    private readonly ITileCacheKeyIndex _keyIndex = keyIndex ?? throw new ArgumentNullException(nameof(keyIndex));
    private readonly TileCacheEvictionOptions _options =
        (tileOptions ?? throw new ArgumentNullException(nameof(tileOptions))).Value.Eviction;
    private readonly ILogger<TileCacheEvictionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ICloudFileStorage? _storage = storage;

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

        var snapshot = await _keyIndex.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Count == 0)
        {
            return new TileCacheEvictionResult(0, 0, Enabled: true);
        }

        var victims = TileCacheQuotaPolicy.SelectEvictions(snapshot, _options);
        if (victims.Count == 0)
        {
            return new TileCacheEvictionResult(snapshot.Count, 0, Enabled: true);
        }

        var evicted = 0;
        foreach (var key in victims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Delete the stored tile first; only drop the index entry once the tile is gone so a
            // failed delete leaves the key tracked for the next sweep instead of orphaning a tile.
            if (_storage is not null)
            {
                try
                {
                    await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.EvictionDeleteFailed(_logger, ex);
                    continue;
                }
            }

            await _keyIndex.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            evicted++;
        }

        Log.EvictionSweepCompleted(_logger, snapshot.Count, evicted);
        return new TileCacheEvictionResult(snapshot.Count, evicted, Enabled: true);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9263, Level = LogLevel.Information, Message = "Tile-cache eviction sweep scanned {Scanned} entries and evicted {Evicted} least-recently-used tiles.")]
        public static partial void EvictionSweepCompleted(ILogger logger, int scanned, int evicted);

        [LoggerMessage(EventId = 9264, Level = LogLevel.Warning, Message = "Failed to delete an evicted tile from the cache store; it remains tracked for the next sweep.")]
        public static partial void EvictionDeleteFailed(ILogger logger, Exception exception);
    }
}
