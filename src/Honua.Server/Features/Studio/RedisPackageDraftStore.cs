// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using StackExchange.Redis;

namespace Honua.Server.Features.Studio;

/// <summary>
/// Shared, bounded Redis storage for Studio map and app package drafts.
/// </summary>
internal sealed class RedisPackageDraftStore : IPackageDraftStore
{
    private const string MapIndexKey = "honua:studio:drafts:{map}:index";
    private const string MapItemPrefix = "honua:studio:drafts:{map}:item:";
    private const string AppIndexKey = "honua:studio:drafts:{app}:index";
    private const string AppItemPrefix = "honua:studio:drafts:{app}:item:";

    private const string SaveScript = """
        redis.call('SET', KEYS[2], ARGV[1], 'PX', ARGV[4])
        redis.call('ZADD', KEYS[1], ARGV[3], ARGV[2])

        local expired = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[3] - ARGV[4])
        for _, id in ipairs(expired) do
            redis.call('DEL', ARGV[6] .. id)
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3] - ARGV[4])

        local excess = redis.call('ZCARD', KEYS[1]) - tonumber(ARGV[5])
        if excess > 0 then
            local evicted = redis.call('ZRANGE', KEYS[1], 0, excess - 1)
            for _, id in ipairs(evicted) do
                redis.call('DEL', ARGV[6] .. id)
                redis.call('ZREM', KEYS[1], id)
            end
        end
        return 1
        """;

    private readonly IDatabase _database;
    private readonly PackageDraftRetentionOptions _retention;
    private readonly TimeProvider _timeProvider;

    public RedisPackageDraftStore(
        IConnectionMultiplexer redis,
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (retention.Ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Draft TTL must be positive.");
        }

        if (retention.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Draft capacity must be positive.");
        }

        _database = redis.GetDatabase();
        _retention = retention;
        _timeProvider = timeProvider;
    }

    public Task SaveMapDraftAsync(MapPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return SaveAsync(
            MapIndexKey,
            MapItemPrefix,
            package.MapPackageId,
            JsonSerializer.Serialize(package, PackagingJsonContext.Default.MapPackage),
            cancellationToken);
    }

    public Task SaveAppDraftAsync(AppPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return SaveAsync(
            AppIndexKey,
            AppItemPrefix,
            package.AppPackageId,
            JsonSerializer.Serialize(package, PackagingJsonContext.Default.AppPackage),
            cancellationToken);
    }

    public async Task<MapPackage?> GetMapDraftAsync(string mapPackageId, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(MapIndexKey, MapItemPrefix, mapPackageId, cancellationToken).ConfigureAwait(false);
        return value.HasValue
            ? JsonSerializer.Deserialize(value.ToString(), PackagingJsonContext.Default.MapPackage)
            : null;
    }

    public async Task<AppPackage?> GetAppDraftAsync(string appPackageId, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(AppIndexKey, AppItemPrefix, appPackageId, cancellationToken).ConfigureAwait(false);
        return value.HasValue
            ? JsonSerializer.Deserialize(value.ToString(), PackagingJsonContext.Default.AppPackage)
            : null;
    }

    private async Task SaveAsync(
        RedisKey indexKey,
        string itemPrefix,
        string id,
        string payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var nowMilliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var ttlMilliseconds = checked((long)_retention.Ttl.TotalMilliseconds);
        await _database.ScriptEvaluateAsync(
            SaveScript,
            [indexKey, (RedisKey)(itemPrefix + id)],
            [payload, id, nowMilliseconds, ttlMilliseconds, _retention.Capacity, itemPrefix]).ConfigureAwait(false);
    }

    private async Task<RedisValue> GetAsync(
        RedisKey indexKey,
        string itemPrefix,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return RedisValue.Null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync(itemPrefix + id).ConfigureAwait(false);
        if (!value.HasValue)
        {
            await _database.SortedSetRemoveAsync(indexKey, id).ConfigureAwait(false);
        }

        return value;
    }
}
