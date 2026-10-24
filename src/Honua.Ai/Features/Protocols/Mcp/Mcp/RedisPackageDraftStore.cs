// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// <see cref="IPackageDraftStore"/> whose records live in Redis. Two instances
/// that share a multiplexer read what the other wrote. Expiry is the key TTL
/// passed to <c>SET</c>, not an in-process sweep. Per-kind capacity still evicts
/// the oldest live draft (honua-server#3312).
/// </summary>
internal sealed class RedisPackageDraftStore : IPackageDraftStore
{
    private readonly PackageDraftLedger _ledger;

    /// <summary>Initializes a new instance of the <see cref="RedisPackageDraftStore"/> class.</summary>
    /// <param name="redis">Shared connection. The database is resolved per call.</param>
    /// <param name="retention">TTL applied as the Redis key expiry, and the per-kind capacity.</param>
    /// <param name="timeProvider">Clock stamped onto each write.</param>
    public RedisPackageDraftStore(
        IConnectionMultiplexer redis,
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _ledger = new PackageDraftLedger(new RedisPackageDraftBacking(redis), retention, timeProvider);
    }

    /// <inheritdoc />
    public Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveMapDraftAsync(package, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveAppDraftAsync(package, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task<MapPackage?> GetMapDraftAsync(
        string mapPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetMapDraftAsync(mapPackageId, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task<AppPackage?> GetAppDraftAsync(
        string appPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetAppDraftAsync(appPackageId, ownerId, cancellationToken);
}

/// <summary>
/// Redis <see cref="IPackageDraftBacking"/>. Data keys carry the JSON record and
/// a native TTL. A sorted set per kind orders live ids for capacity eviction.
/// The index key is not a data key, so an identifier cannot overwrite it.
/// </summary>
internal sealed class RedisPackageDraftBacking : IPackageDraftBacking
{
    private const string Prefix = "honua:package-draft:v1:";

    private readonly IConnectionMultiplexer _redis;

    public RedisPackageDraftBacking(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        PackageDraftKind kind,
        string id,
        PackageDraftRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        if (ttl <= TimeSpan.Zero)
        {
            await RemoveAsync(kind, id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var database = _redis.GetDatabase();
        await database.StringSetAsync(DataKey(kind, id), Encode(kind, record), ttl).ConfigureAwait(false);
        await database.SortedSetAddAsync(IndexKey(kind), id, record.WrittenAt.ToUnixTimeMilliseconds())
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackageDraftRecord?> TryGetAsync(
        PackageDraftKind kind,
        string id,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero)
        {
            await RemoveAsync(kind, id, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var database = _redis.GetDatabase();
        var payload = await database.StringGetAsync(DataKey(kind, id)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            await database.SortedSetRemoveAsync(IndexKey(kind), id).ConfigureAwait(false);
            return null;
        }

        var record = Decode(kind, payload.ToString());
        if (record is null)
        {
            await RemoveAsync(kind, id, cancellationToken).ConfigureAwait(false);
        }

        return record;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageDraftLiveEntry>> ListLiveOldestFirstAsync(
        PackageDraftKind kind,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = _redis.GetDatabase();
        var index = IndexKey(kind);
        var members = await database
            .SortedSetRangeByRankWithScoresAsync(index, 0, -1, Order.Ascending)
            .ConfigureAwait(false);
        if (members.Length == 0 || ttl <= TimeSpan.Zero)
        {
            if (members.Length > 0)
            {
                await DeleteMembersAsync(database, kind, members).ConfigureAwait(false);
            }

            return [];
        }

        var batch = database.CreateBatch();
        var exists = new Task<bool>[members.Length];
        for (var indexInPage = 0; indexInPage < members.Length; indexInPage++)
        {
            exists[indexInPage] = batch.KeyExistsAsync(DataKey(kind, members[indexInPage].Element.ToString()));
        }

        batch.Execute();
        await Task.WhenAll(exists).ConfigureAwait(false);

        var live = new List<PackageDraftLiveEntry>(members.Length);
        var dead = new List<RedisValue>();
        for (var indexInPage = 0; indexInPage < members.Length; indexInPage++)
        {
            var id = members[indexInPage].Element.ToString();
            if (string.IsNullOrEmpty(id) || !await exists[indexInPage].ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(id))
                {
                    dead.Add(members[indexInPage].Element);
                }

                continue;
            }

            live.Add(new PackageDraftLiveEntry(
                id,
                DateTimeOffset.FromUnixTimeMilliseconds((long)members[indexInPage].Score)));
        }

        if (dead.Count > 0)
        {
            await database.SortedSetRemoveAsync(index, [.. dead]).ConfigureAwait(false);
        }

        return live;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = _redis.GetDatabase();
        await database.KeyDeleteAsync(DataKey(kind, id)).ConfigureAwait(false);
        await database.SortedSetRemoveAsync(IndexKey(kind), id).ConfigureAwait(false);
    }

    /// <summary>Serializes a record. Used by tests to pin the wire shape without a Redis connection.</summary>
    internal static string Encode(PackageDraftKind kind, PackageDraftRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var packageJson = kind switch
        {
            PackageDraftKind.Map => JsonSerializer.Serialize(record.Map, PackagingJsonContext.Default.MapPackage),
            PackageDraftKind.App => JsonSerializer.Serialize(record.App, PackagingJsonContext.Default.AppPackage),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var wire = new PackageDraftWire(PackageDraftWire.CurrentVersion, record.OwnerId, record.WrittenAt, packageJson);
        return JsonSerializer.Serialize(wire, PackageDraftRedisJsonContext.Default.PackageDraftWire);
    }

    /// <summary>
    /// Deserializes a record. Returns null for a missing, foreign-version, or
    /// unreadable payload. Does not throw for corrupt JSON.
    /// </summary>
    internal static PackageDraftRecord? Decode(PackageDraftKind kind, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var wire = JsonSerializer.Deserialize(payload, PackageDraftRedisJsonContext.Default.PackageDraftWire);
            if (wire is null
                || wire.Version != PackageDraftWire.CurrentVersion
                || string.IsNullOrWhiteSpace(wire.OwnerId)
                || string.IsNullOrWhiteSpace(wire.PackageJson))
            {
                return null;
            }

            return kind switch
            {
                PackageDraftKind.Map => ReadMap(wire),
                PackageDraftKind.App => ReadApp(wire),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PackageDraftRecord? ReadMap(PackageDraftWire wire)
    {
        var package = JsonSerializer.Deserialize(wire.PackageJson, PackagingJsonContext.Default.MapPackage);
        return package is null ? null : new PackageDraftRecord(wire.OwnerId, wire.WrittenAt, package, null);
    }

    private static PackageDraftRecord? ReadApp(PackageDraftWire wire)
    {
        var package = JsonSerializer.Deserialize(wire.PackageJson, PackagingJsonContext.Default.AppPackage);
        return package is null ? null : new PackageDraftRecord(wire.OwnerId, wire.WrittenAt, null, package);
    }

    private static async Task DeleteMembersAsync(IDatabase database, PackageDraftKind kind, SortedSetEntry[] members)
    {
        var keys = new RedisKey[members.Length];
        var ids = new RedisValue[members.Length];
        for (var index = 0; index < members.Length; index++)
        {
            var id = members[index].Element.ToString();
            keys[index] = DataKey(kind, id);
            ids[index] = members[index].Element;
        }

        await database.KeyDeleteAsync(keys).ConfigureAwait(false);
        await database.SortedSetRemoveAsync(IndexKey(kind), ids).ConfigureAwait(false);
    }

    private static RedisKey DataKey(PackageDraftKind kind, string id) =>
        Prefix + "data:" + KindName(kind) + ":" + id;

    private static RedisKey IndexKey(PackageDraftKind kind) =>
        Prefix + "index:" + KindName(kind);

    private static string KindName(PackageDraftKind kind) => kind switch
    {
        PackageDraftKind.Map => "map",
        PackageDraftKind.App => "app",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>JSON envelope stored at a package-draft data key.</summary>
/// <param name="Version">Schema version. Unknown versions read as not found.</param>
/// <param name="OwnerId">Caller subject allowed to read the draft.</param>
/// <param name="WrittenAt">Utc instant of the write that started the TTL.</param>
/// <param name="PackageJson">Package body serialized with <see cref="PackagingJsonContext"/>.</param>
internal sealed record PackageDraftWire(int Version, string OwnerId, DateTimeOffset WrittenAt, string PackageJson)
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Source-generated serializer for <see cref="PackageDraftWire"/>.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageDraftWire))]
internal sealed partial class PackageDraftRedisJsonContext : JsonSerializerContext;
