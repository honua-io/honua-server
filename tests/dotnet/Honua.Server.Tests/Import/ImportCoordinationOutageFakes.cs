// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Migration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

/// <summary>
/// In-process stand-in for the Redis connection used by import coordination. Lists, locks
/// and sets behave like Redis so jobs really flow through the ready/processing lists, and
/// <see cref="Down"/> fails every command (ping included) like a lost connection. Nothing
/// here touches a shared Redis instance (#4877).
/// </summary>
internal sealed class OutageRedis
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<string>> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal);
    private int _pingCount;

    public OutageRedis()
    {
        Database = Substitute.For<IDatabase>();
        Database.PingAsync(Arg.Any<CommandFlags>())
            .Returns(_ => Run(() =>
            {
                _pingCount++;
                return TimeSpan.FromMilliseconds(1);
            }));
        Database.ListLeftPushAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() =>
            {
                var list = List(call.ArgAt<RedisKey>(0));
                list.AddFirst(call.ArgAt<RedisValue>(1).ToString());
                return (long)list.Count;
            }));
        Database.ListRightPopLeftPushAsync(Arg.Any<RedisKey>(), Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() =>
            {
                var source = List(call.ArgAt<RedisKey>(0));
                if (source.Last is null)
                {
                    return RedisValue.Null;
                }

                var value = source.Last.Value;
                source.RemoveLast();
                List(call.ArgAt<RedisKey>(1)).AddFirst(value);
                return (RedisValue)value;
            }));
        Database.ListLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() => (long)List(call.ArgAt<RedisKey>(0)).Count));
        Database.ListRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() => List(call.ArgAt<RedisKey>(0)).Remove(call.ArgAt<RedisValue>(1).ToString()) ? 1L : 0L));
        Database.LockTakeAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() =>
            {
                // SET NX semantics: a held lock is never re-taken, even by its owner.
                return _locks.TryAdd(call.ArgAt<RedisKey>(0).ToString(), call.ArgAt<RedisValue>(1).ToString());
            }));
        Database.LockExtendAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() =>
                _locks.TryGetValue(call.ArgAt<RedisKey>(0).ToString(), out var holder) &&
                holder == call.ArgAt<RedisValue>(1).ToString()));
        Database.LockReleaseAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                return _locks.TryGetValue(key, out var holder) &&
                    holder == call.ArgAt<RedisValue>(1).ToString() &&
                    _locks.Remove(key);
            }));
        Database.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() => Set(call.ArgAt<RedisKey>(0)).Add(call.ArgAt<RedisValue>(1).ToString())));
        Database.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() => Set(call.ArgAt<RedisKey>(0)).Remove(call.ArgAt<RedisValue>(1).ToString())));
        Database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => Run(() => Set(call.ArgAt<RedisKey>(0)).Select(member => (RedisValue)member).ToArray()));

        Multiplexer = Substitute.For<IConnectionMultiplexer>();
        Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Database);
    }

    public IConnectionMultiplexer Multiplexer { get; }

    public IDatabase Database { get; }

    public bool Down { get; set; }

    public int PingCount
    {
        get
        {
            lock (_gate)
            {
                return _pingCount;
            }
        }
    }

    public IReadOnlyList<string> ListContents(string key)
    {
        lock (_gate)
        {
            return List(key).ToArray();
        }
    }

    public IReadOnlyCollection<string> SetContents(string key)
    {
        lock (_gate)
        {
            return Set(key).ToArray();
        }
    }

    private Task<T> Run<T>(Func<T> command)
    {
        lock (_gate)
        {
            return Down
                ? Task.FromException<T>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "Simulated Redis outage"))
                : Task.FromResult(command());
        }
    }

    private LinkedList<string> List(RedisKey key)
    {
        var name = key.ToString();
        if (!_lists.TryGetValue(name, out var list))
        {
            list = new LinkedList<string>();
            _lists[name] = list;
        }

        return list;
    }

    private HashSet<string> Set(RedisKey key)
    {
        var name = key.ToString();
        if (!_sets.TryGetValue(name, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _sets[name] = set;
        }

        return set;
    }
}

/// <summary>
/// Distributed cache whose availability is switched independently of <see cref="OutageRedis"/>,
/// so a test can restore the Redis connection while the request/progress cache stays down.
/// </summary>
internal sealed class OutageDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _writeCount;

    public bool Down { get; set; }

    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return _writeCount;
            }
        }
    }

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate)
            {
                return _entries.Keys.ToArray();
            }
        }
    }

    public byte[]? Get(string key)
    {
        lock (_gate)
        {
            ThrowIfDown();
            return _entries.TryGetValue(key, out var value) ? value.ToArray() : null;
        }
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Get(key));
        }
        catch (RedisConnectionException ex)
        {
            return Task.FromException<byte[]?>(ex);
        }
    }

    public void Refresh(string key)
    {
        lock (_gate)
        {
            ThrowIfDown();
        }
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        Refresh(key);
        return Task.CompletedTask;
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            ThrowIfDown();
            _entries.Remove(key);
        }
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        try
        {
            Remove(key);
            return Task.CompletedTask;
        }
        catch (RedisConnectionException ex)
        {
            return Task.FromException(ex);
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        lock (_gate)
        {
            ThrowIfDown();
            _writeCount++;
            _entries[key] = value.ToArray();
        }
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        try
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
        catch (RedisConnectionException ex)
        {
            return Task.FromException(ex);
        }
    }

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Simulated cache outage");
        }
    }
}

/// <summary>
/// Production-mode import coordination wired to the outage doubles above, exposing the
/// concrete components so tests can elapse their existing 30-second retry throttles.
/// </summary>
internal sealed class OutageCoordination
{
    private readonly object[] _throttledComponents;

    private OutageCoordination(
        OutageRedis redis,
        OutageDistributedCache cache,
        UniversalProgressStore universalProgressStore,
        object jobManager,
        params object[] throttledComponents)
    {
        Redis = redis;
        Cache = cache;
        UniversalProgressStore = universalProgressStore;
        JobManager = jobManager;
        _throttledComponents = [universalProgressStore, .. throttledComponents];
    }

    public OutageRedis Redis { get; }

    public OutageDistributedCache Cache { get; }

    public UniversalProgressStore UniversalProgressStore { get; }

    public object JobManager { get; }

    public static OutageCoordination ForGeoservices()
    {
        var redis = new OutageRedis();
        var cache = new OutageDistributedCache();
        var universal = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
        var manager = new RedisImportJobManager(
            universal,
            cache,
            NullLogger<RedisImportJobManager>.Instance,
            new StaticHostEnvironment("Production"),
            redis.Multiplexer);
        return new OutageCoordination(redis, cache, universal, manager, manager.JobQueue, manager.LeaderElection, manager.RequestStore);
    }

    public static OutageCoordination ForGeoServer()
    {
        var redis = new OutageRedis();
        var cache = new OutageDistributedCache();
        var universal = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance);
        var manager = new GeoServerImportJobManager(
            universal,
            cache,
            NullLogger<GeoServerImportJobManager>.Instance,
            new StaticHostEnvironment("Production"),
            redis.Multiplexer);
        return new OutageCoordination(redis, cache, universal, manager, manager.JobQueue, manager.LeaderElection, manager.RequestStore);
    }

    public void BeginOutage()
    {
        Redis.Down = true;
        Cache.Down = true;
    }

    public void EndOutage()
    {
        Redis.Down = false;
        Cache.Down = false;
    }

    /// <summary>
    /// Elapses each component's existing retry interval without sleeping 30 seconds.
    /// </summary>
    public void ElapseRetryThrottles()
    {
        foreach (var component in _throttledComponents)
        {
            var field = component.GetType().GetField("_lastRedisFailure", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{component.GetType().Name} has no retry timestamp.");
            field.SetValue(component, DateTime.MinValue);
        }
    }
}

internal static class ImportWorkerTestServices
{
    /// <summary>
    /// Removes the hosted import workers so only the start endpoint can probe recovery,
    /// modelling a node whose worker is busy with a long job when Redis returns.
    /// </summary>
    public static void RemoveHostedImportWorkers(IServiceCollection services)
    {
        Type[] workers =
        [
            typeof(GeoservicesImportBackgroundService),
            typeof(GeoServerImportBackgroundService),
            typeof(MigrationBatchBackgroundService)
        ];
        foreach (var descriptor in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                                          workers.Contains(descriptor.ImplementationType))
                     .ToList())
        {
            services.Remove(descriptor);
        }
    }
}
