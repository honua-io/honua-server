// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Tiles;
using StackExchange.Redis;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Redis-backed <see cref="ITileCacheKeyIndex" /> that maintains a live last-access index for the
/// tile cache so the size-quota / LRU evictor can drop the least-recently-used tiles on the hot
/// serve path (#1917). Two Redis structures keyed off a shared prefix back the index:
/// <list type="bullet">
///   <item><description>a sorted set whose score is the last-access Unix-millisecond timestamp,
///   giving cheap LRU-ordered range scans; and</description></item>
///   <item><description>a hash of <c>key → sizeBytes</c> so the evictor can honor the configured
///   byte-size quota without re-reading every tile.</description></item>
/// </list>
/// Index updates are best-effort bookkeeping: any Redis failure is swallowed so a tile request never
/// fails because of eviction accounting. This is the canonical binding that replaces relying solely
/// on the Redis server <c>maxmemory-policy allkeys-lru</c> setting.
/// </summary>
internal sealed partial class RedisTileCacheKeyIndex : ITileCacheKeyIndex
{
    private const string LastAccessSetKey = "honua:tile-cache:lru";
    private const string SizeHashKey = "honua:tile-cache:size";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisTileCacheKeyIndex> _logger;

    public RedisTileCacheKeyIndex(IConnectionMultiplexer redis, ILogger<RedisTileCacheKeyIndex> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();
            var score = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Refresh LRU score and remember the byte size. A transaction keeps the two structures
            // consistent so the evictor never sees a key without an associated size.
            var transaction = db.CreateTransaction();
            _ = transaction.SortedSetAddAsync(LastAccessSetKey, key, score);
            _ = transaction.HashSetAsync(SizeHashKey, key, sizeBytes);
            await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Eviction accounting must never fail a tile request.
            Log.RecordAccessFailed(_logger, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();

            // Least-recently-used first so the snapshot ordering already matches the policy's
            // eviction order; the policy re-sorts defensively but this keeps payloads small.
            var ranked = await db.SortedSetRangeByRankWithScoresAsync(
                LastAccessSetKey,
                0,
                -1,
                Order.Ascending).ConfigureAwait(false);

            if (ranked.Length == 0)
            {
                return [];
            }

            var fields = new RedisValue[ranked.Length];
            for (var i = 0; i < ranked.Length; i++)
            {
                fields[i] = ranked[i].Element;
            }

            var sizes = await db.HashGetAsync(SizeHashKey, fields).ConfigureAwait(false);

            var entries = new List<TileCacheEntry>(ranked.Length);
            for (var i = 0; i < ranked.Length; i++)
            {
                var key = (string?)ranked[i].Element;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var size = sizes[i].TryParse(out long parsed) ? parsed : 0L;
                var lastAccess = DateTimeOffset.FromUnixTimeMilliseconds((long)ranked[i].Score);
                entries.Add(new TileCacheEntry(key, size, lastAccess));
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SnapshotFailed(_logger, ex);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();
            var transaction = db.CreateTransaction();
            _ = transaction.SortedSetRemoveAsync(LastAccessSetKey, key);
            _ = transaction.HashDeleteAsync(SizeHashKey, key);
            await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RemoveFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9260, Level = LogLevel.Debug, Message = "Failed to record tile-cache access in Redis LRU index; eviction accounting skipped for this tile.")]
        public static partial void RecordAccessFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9261, Level = LogLevel.Warning, Message = "Failed to snapshot the Redis tile-cache LRU index; eviction sweep will be skipped.")]
        public static partial void SnapshotFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9262, Level = LogLevel.Debug, Message = "Failed to remove a key from the Redis tile-cache LRU index.")]
        public static partial void RemoveFailed(ILogger logger, Exception exception);
    }
}
