// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Proves the Metadata v2 graph snapshot cache is invalidated on every node of a multi-node
/// deployment: a catalog write on one node broadcasts on Redis pub/sub and the peer drops its
/// cached snapshot instead of serving a stale graph (a freshly published service 404ing on the
/// other node) until the TTL expires.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisMetadataV2GraphCacheInvalidatorTests
{
    private const string Env = "default";

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task WriteOnOneNode_DropsTheSnapshotCachedOnThePeerNode()
    {
        var bus = new FakeRedisBus();
        var (nodeA, cacheA) = NewNode(bus, "node-a");
        var (nodeB, cacheB) = NewNode(bus, "node-b");
        await nodeA.StartAsync(CancellationToken.None);
        await nodeB.StartAsync(CancellationToken.None);

        var store = new CountingStore(Snapshot(1));
        (await cacheB.GetOrLoadAsync(Env, store.GetCurrentAsync)).Revision.Should().Be(1);

        // Node A commits a catalog write (e.g. a service publish); the store calls the invalidator.
        store.Current = Snapshot(2);
        nodeA.Invalidate(Env);

        // Node B must reload rather than serve revision 1 for the rest of the 60 s TTL.
        (await cacheB.GetOrLoadAsync(Env, store.GetCurrentAsync)).Revision.Should().Be(2);
        store.Calls.Should().Be(2);
        bus.Published.Should().ContainSingle()
            .Which.Should().Be("node-a\n" + Env);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateAll_ClearsEveryEnvironmentOnThePeerNode()
    {
        var bus = new FakeRedisBus();
        var (nodeA, _) = NewNode(bus, "node-a");
        var (nodeB, cacheB) = NewNode(bus, "node-b");
        await nodeB.StartAsync(CancellationToken.None);

        var store = new CountingStore(Snapshot(1));
        await cacheB.GetOrLoadAsync("one", store.GetCurrentAsync);
        await cacheB.GetOrLoadAsync("two", store.GetCurrentAsync);

        nodeA.InvalidateAll();

        await cacheB.GetOrLoadAsync("one", store.GetCurrentAsync);
        await cacheB.GetOrLoadAsync("two", store.GetCurrentAsync);
        store.Calls.Should().Be(4);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task OwnMessage_IsIgnored_AndOtherEnvironmentsAreUntouched()
    {
        var bus = new FakeRedisBus();
        var (node, cache) = NewNode(bus, "node-a");
        var store = new CountingStore(Snapshot(1));
        await cache.GetOrLoadAsync(Env, store.GetCurrentAsync);
        await cache.GetOrLoadAsync("other", store.GetCurrentAsync);

        node.OnMessage(RedisChannel.Literal(node.ChannelName), "node-a\n" + Env);
        node.OnMessage(RedisChannel.Literal(node.ChannelName), "node-b\n" + Env);

        await cache.GetOrLoadAsync(Env, store.GetCurrentAsync);
        await cache.GetOrLoadAsync("other", store.GetCurrentAsync);
        store.Calls.Should().Be(3, "only the peer's message for this environment invalidates");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InstallationsWithDistinctKeyPrefixes_SharingOneRedis_DoNotInvalidateEachOther()
    {
        var bus = new FakeRedisBus();
        var (tenantA, _) = NewNode(bus, "a-1", keyPrefix: "tenant-a:");
        var (tenantB, cacheB) = NewNode(bus, "b-1", keyPrefix: "tenant-b");
        await tenantB.StartAsync(CancellationToken.None);

        var store = new CountingStore(Snapshot(1));
        await cacheB.GetOrLoadAsync(Env, store.GetCurrentAsync);

        tenantA.Invalidate(Env);

        await cacheB.GetOrLoadAsync(Env, store.GetCurrentAsync);
        store.Calls.Should().Be(1, "a write in another installation must not evict this installation's snapshot");
        tenantA.ChannelName.Should().Be("tenant-a:metadata:v2:graph:invalidations");
        tenantB.ChannelName.Should().Be("tenant-b:metadata:v2:graph:invalidations");
        RedisMetadataV2GraphCacheInvalidator.BuildChannelName(null)
            .Should().Be("honua:metadata:v2:graph:invalidations");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void MalformedMessage_IsDroppedWithoutThrowing()
    {
        var (node, _) = NewNode(new FakeRedisBus(), "node-a");

        var act = () =>
        {
            node.OnMessage(RedisChannel.Literal(node.ChannelName), "no-separator");
            node.OnMessage(RedisChannel.Literal(node.ChannelName), RedisValue.Null);
        };

        act.Should().NotThrow();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RedisFailures_NeverFailTheWriteOrStartup_AndTheLocalCacheIsStillCleared()
    {
        var subscriber = Substitute.For<ISubscriber>();
        subscriber.Publish(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        var cache = NewCache();
        var node = new RedisMetadataV2GraphCacheInvalidator(
            cache, redis, NullLogger<RedisMetadataV2GraphCacheInvalidator>.Instance, "node-a");

        await node.Invoking(n => n.StartAsync(CancellationToken.None)).Should().NotThrowAsync();

        var store = new CountingStore(Snapshot(1));
        await cache.GetOrLoadAsync(Env, store.GetCurrentAsync);
        node.Invoking(n => n.Invalidate(Env)).Should().NotThrow();
        await cache.GetOrLoadAsync(Env, store.GetCurrentAsync);
        store.Calls.Should().Be(2, "the writing node's own cache is cleared even when the broadcast fails");
    }







    private static (RedisMetadataV2GraphCacheInvalidator Node, MetadataV2GraphSnapshotCache Cache) NewNode(
        FakeRedisBus bus,
        string instanceId,
        string? keyPrefix = null)
    {
        var cache = NewCache();
        var node = new RedisMetadataV2GraphCacheInvalidator(
            cache,
            bus.Connect(),
            NullLogger<RedisMetadataV2GraphCacheInvalidator>.Instance,
            instanceId,
            keyPrefix);
        return (node, cache);
    }

    private static MetadataV2GraphSnapshotCache NewCache() =>
        new(Options.Create(new CacheOptions { MetadataGraphCacheEnabled = true, MetadataGraphTtlSeconds = 60 }));

    private static MetadataV2GraphSnapshot Snapshot(long revision)
    {
        var graph = new MetadataV2Graph
        {
            Environment = Env,
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        return new MetadataV2GraphSnapshot(graph, $"\"rev-{revision}\"", DateTimeOffset.UtcNow);
    }

    private sealed class CountingStore(MetadataV2GraphSnapshot initial)
    {
        private int _calls;

        public MetadataV2GraphSnapshot Current { get; set; } = initial;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(Current);
        }
    }

    /// <summary>
    /// In-memory stand-in for one Redis server's pub/sub: every node's multiplexer shares the same
    /// subscription table, and a publish synchronously delivers to every subscriber on the channel.
    /// </summary>
    private sealed class FakeRedisBus
    {
        private readonly List<(RedisChannel Channel, Action<RedisChannel, RedisValue> Handler)> _subscriptions = [];

        public List<string> Published { get; } = [];

        public IConnectionMultiplexer Connect()
        {
            var subscriber = Substitute.For<ISubscriber>();
            subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    _subscriptions.Add((call.ArgAt<RedisChannel>(0), call.ArgAt<Action<RedisChannel, RedisValue>>(1)));
                    return Task.CompletedTask;
                });
            subscriber.Publish(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call =>
                {
                    var channel = call.ArgAt<RedisChannel>(0);
                    var message = call.ArgAt<RedisValue>(1);
                    Published.Add(message.ToString());
                    var delivered = 0L;
                    foreach (var (subscribedChannel, handler) in _subscriptions.ToArray())
                    {
                        if (subscribedChannel == channel)
                        {
                            handler(channel, message);
                            delivered++;
                        }
                    }

                    return delivered;
                });

            var redis = Substitute.For<IConnectionMultiplexer>();
            redis.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
            return redis;
        }
    }
}
