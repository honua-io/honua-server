// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

internal sealed class RedisPackageDraftStore : IPackageDraftStore
{
    private readonly PackageDraftLedger _ledger;

    public RedisPackageDraftStore(
        IConnectionMultiplexer redis,
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _ledger = new PackageDraftLedger(new RedisPackageDraftBacking(redis), retention, timeProvider);
    }

    public Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveMapDraftAsync(package, ownerId, cancellationToken);

    public Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveAppDraftAsync(package, ownerId, cancellationToken);

    public Task<MapPackage?> GetMapDraftAsync(
        string mapPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetMapDraftAsync(mapPackageId, ownerId, cancellationToken);

    public Task<AppPackage?> GetAppDraftAsync(
        string appPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetAppDraftAsync(appPackageId, ownerId, cancellationToken);
}

// The index key is not a data key, so a package identifier cannot overwrite it.
internal sealed class RedisPackageDraftBacking : IPackageDraftBacking
{
    private const string Prefix = "honua:package-draft:v1:";

    private readonly IConnectionMultiplexer _redis;

    public RedisPackageDraftBacking(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
    }

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

    public async Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = _redis.GetDatabase();
        await database.KeyDeleteAsync(DataKey(kind, id)).ConfigureAwait(false);
        await database.SortedSetRemoveAsync(IndexKey(kind), id).ConfigureAwait(false);
    }

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

internal sealed record PackageDraftWire(int Version, string OwnerId, DateTimeOffset WrittenAt, string PackageJson)
{
    public const int CurrentVersion = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageDraftWire))]
internal sealed partial class PackageDraftRedisJsonContext : JsonSerializerContext;
