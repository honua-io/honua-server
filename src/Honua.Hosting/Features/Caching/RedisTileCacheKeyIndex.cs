// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
internal sealed partial class RedisTileCacheKeyIndex : ITileCacheKeyIndex, ITileCacheMutationCoordinator
{
    private const string LastAccessSetKey = "honua:tile-cache:lru";
    private const string SizeHashKey = "honua:tile-cache:size";
    private const string WriteVersionHashKey = "honua:tile-cache:write-version";
    private const string ExpiredSetKey = "honua:tile-cache:expired";
    private const string MutationLeaseKeyPrefix = "honua:tile-cache:mutation:";
    private static readonly TimeSpan DefaultMutationLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMutationLeaseRenewalInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MutationLeaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private const string SnapshotScript = """
        local ranked = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local snapshot = {}
        for index = 1, #ranked, 2 do
            local key = ranked[index]
            snapshot[#snapshot + 1] = key
            snapshot[#snapshot + 1] = ranked[index + 1]
            snapshot[#snapshot + 1] = redis.call('HGET', KEYS[2], key) or ''
            snapshot[#snapshot + 1] = redis.call('HGET', KEYS[3], key) or ''
        end
        return snapshot
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisTileCacheKeyIndex> _logger;
    private readonly TimeSpan _mutationLeaseDuration;
    private readonly TimeSpan _mutationLeaseRenewalInterval;

    public RedisTileCacheKeyIndex(IConnectionMultiplexer redis, ILogger<RedisTileCacheKeyIndex> logger)
        : this(redis, logger, DefaultMutationLeaseDuration, DefaultMutationLeaseRenewalInterval)
    {
    }

    internal RedisTileCacheKeyIndex(
        IConnectionMultiplexer redis,
        ILogger<RedisTileCacheKeyIndex> logger,
        TimeSpan mutationLeaseDuration,
        TimeSpan mutationLeaseRenewalInterval)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mutationLeaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mutationLeaseRenewalInterval, TimeSpan.Zero);
        if (mutationLeaseRenewalInterval >= mutationLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutationLeaseRenewalInterval),
                "The mutation lease must be renewed before it expires.");
        }

        _mutationLeaseDuration = mutationLeaseDuration;
        _mutationLeaseRenewalInterval = mutationLeaseRenewalInterval;
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
        => await RecordAsync(key, sizeBytes, clearExpiration: false, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RecordWriteAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
        => await RecordAsync(key, sizeBytes, clearExpiration: true, cancellationToken).ConfigureAwait(false);

    private async Task RecordAsync(
        string key,
        long sizeBytes,
        bool clearExpiration,
        CancellationToken cancellationToken)
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
            if (clearExpiration)
            {
                _ = transaction.HashSetAsync(WriteVersionHashKey, key, Guid.NewGuid().ToString("N"));
                _ = transaction.SetRemoveAsync(ExpiredSetKey, key);
            }

            await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Eviction accounting must never fail a tile request.
            Log.RecordAccessFailed(_logger, ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await _redis.GetDatabase().SetContainsAsync(ExpiredSetKey, key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ExpirationReadFailed(_logger, ex);
            // Fail closed: an unavailable lifecycle index must never allow bytes that may have
            // been explicitly expired to be served as a cache hit.
            return true;
        }
    }

    /// <inheritdoc />
    public async Task MarkExpiredAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _redis.GetDatabase().SetAddAsync(ExpiredSetKey, key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ExpirationWriteFailed(_logger, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
        => (await SnapshotWithStatusAsync(cancellationToken).ConfigureAwait(false)).Entries;

    /// <inheritdoc />
    public async Task<TileCacheIndexSnapshot> SnapshotWithStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();

            // Capture each ranked observation together with its size and write generation in one
            // Redis command. A later write cannot otherwise slip between ZRANGE and HGET and make
            // lifecycle deletion mistake a newly generated object for the older snapshot entry.
            var snapshot = (RedisResult[]?)await db.ScriptEvaluateAsync(
                SnapshotScript,
                new RedisKey[] { LastAccessSetKey, SizeHashKey, WriteVersionHashKey },
                values: null,
                flags: CommandFlags.DemandMaster).ConfigureAwait(false);
            if (snapshot is null || snapshot.Length == 0)
            {
                return new TileCacheIndexSnapshot([], IsAvailable: true);
            }

            const int fieldsPerEntry = 4;
            if (snapshot.Length % fieldsPerEntry != 0)
            {
                throw new RedisException("Tile-cache snapshot script returned a malformed result.");
            }

            var entries = new List<TileCacheEntry>(snapshot.Length / fieldsPerEntry);
            for (var i = 0; i < snapshot.Length; i += fieldsPerEntry)
            {
                var key = (string?)snapshot[i];
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var scoreRaw = (string?)snapshot[i + 1];
                if (!double.TryParse(scoreRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                {
                    throw new RedisException("Tile-cache snapshot script returned an invalid LRU score.");
                }

                var sizeRaw = (string?)snapshot[i + 2];
                var size = long.TryParse(sizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0L;
                var lastAccess = DateTimeOffset.FromUnixTimeMilliseconds((long)score);
                var writeVersion = (string?)snapshot[i + 3];
                if (string.IsNullOrEmpty(writeVersion))
                {
                    writeVersion = null;
                }

                entries.Add(new TileCacheEntry(key, size, lastAccess, writeVersion));
            }

            return new TileCacheIndexSnapshot(entries, IsAvailable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SnapshotFailed(_logger, ex);
            return new TileCacheIndexSnapshot([], IsAvailable: false);
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
            _ = transaction.HashDeleteAsync(WriteVersionHashKey, key);
            _ = transaction.SetRemoveAsync(ExpiredSetKey, key);
            await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RemoveFailed(_logger, ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRemoveAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = _redis.GetDatabase();
            var transaction = db.CreateTransaction();
            transaction.AddCondition(entry.WriteVersion is null
                ? Condition.HashNotExists(WriteVersionHashKey, entry.Key)
                : Condition.HashEqual(WriteVersionHashKey, entry.Key, entry.WriteVersion));
            _ = transaction.SortedSetRemoveAsync(LastAccessSetKey, entry.Key);
            _ = transaction.HashDeleteAsync(SizeHashKey, entry.Key);
            _ = transaction.HashDeleteAsync(WriteVersionHashKey, entry.Key);
            _ = transaction.SetRemoveAsync(ExpiredSetKey, entry.Key);
            return await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RemoveFailed(_logger, ex);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ExecuteSerializedAsync(
        string key,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(mutation);

        var database = _redis.GetDatabase();
        var leaseKey = MutationLeaseKeyPrefix + key;
        var owner = Guid.NewGuid().ToString("N");
        while (!await database.LockTakeAsync(leaseKey, owner, _mutationLeaseDuration).ConfigureAwait(false))
        {
            await Task.Delay(MutationLeaseRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        using var renewalStop = new CancellationTokenSource();
        using var leaseLost = new CancellationTokenSource();
        using var mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            leaseLost.Token);
        var renewalTask = RenewMutationLeaseAsync(
            database,
            leaseKey,
            owner,
            leaseLost,
            renewalStop.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await mutation(mutationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                leaseLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Lost the distributed tile-cache mutation lease for key '{key}'.",
                    ex);
            }
        }
        finally
        {
            await renewalStop.CancelAsync().ConfigureAwait(false);
            await renewalTask.ConfigureAwait(false);
            await database.LockReleaseAsync(leaseKey, owner).ConfigureAwait(false);
        }

        if (leaseLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Lost the distributed tile-cache mutation lease for key '{key}'.");
        }
    }

    private async Task RenewMutationLeaseAsync(
        IDatabase database,
        RedisKey leaseKey,
        RedisValue owner,
        CancellationTokenSource leaseLost,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_mutationLeaseRenewalInterval, cancellationToken).ConfigureAwait(false);
                bool extended;
                try
                {
                    extended = await database.LockExtendAsync(leaseKey, owner, _mutationLeaseDuration)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    Log.MutationLeaseRenewalFailed(_logger, leaseKey.ToString()!, ex);
                    await leaseLost.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (!extended)
                {
                    Log.MutationLeaseLost(_logger, leaseKey.ToString()!);
                    await leaseLost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the serialized mutation completes or fails.
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsCurrentAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(entry.Key))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var database = _redis.GetDatabase();
        var score = await database.SortedSetScoreAsync(LastAccessSetKey, entry.Key).ConfigureAwait(false);
        if (!score.HasValue)
        {
            return false;
        }

        var currentVersion = await database.HashGetAsync(WriteVersionHashKey, entry.Key).ConfigureAwait(false);
        return entry.WriteVersion is null
            ? currentVersion.IsNull
            : string.Equals((string?)currentVersion, entry.WriteVersion, StringComparison.Ordinal);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9260, Level = LogLevel.Debug, Message = "Failed to record tile-cache access in Redis LRU index; eviction accounting skipped for this tile.")]
        public static partial void RecordAccessFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9261, Level = LogLevel.Warning, Message = "Failed to snapshot the Redis tile-cache LRU index; eviction sweep will be skipped.")]
        public static partial void SnapshotFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9262, Level = LogLevel.Debug, Message = "Failed to remove a key from the Redis tile-cache LRU index.")]
        public static partial void RemoveFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9266, Level = LogLevel.Debug, Message = "Failed to read a tile-cache expiration marker from Redis.")]
        public static partial void ExpirationReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9267, Level = LogLevel.Warning, Message = "Failed to write a tile-cache expiration marker to Redis.")]
        public static partial void ExpirationWriteFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9268, Level = LogLevel.Warning, Message = "Lost Redis tile-cache mutation lease {LeaseKey} during a storage mutation.")]
        public static partial void MutationLeaseLost(ILogger logger, string leaseKey);

        [LoggerMessage(EventId = 9269, Level = LogLevel.Warning, Message = "Failed to renew Redis tile-cache mutation lease {LeaseKey}; cancelling the storage mutation.")]
        public static partial void MutationLeaseRenewalFailed(ILogger logger, string leaseKey, Exception exception);
    }
}
