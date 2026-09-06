// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamTenantIsolationTests
{
    [Theory]
    [InlineData("tenant-b", "schema_a")]
    [InlineData("tenant-a", "schema_b")]
    [InlineData(null, "schema_a")]
    [InlineData("tenant-a", null)]
    [Trait("Tier", "Fast")]
    public void PublishObservations_DifferentScope_DoesNotDeliverEvenWithCollidingIds(string? tenant, string? schema)
    {
        using var manager = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance);
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        using var own = manager.TryCreateSession("SSE", 1, scope)!;
        using var other = manager.TryCreateSession("SSE", 1, new(tenant, schema))!;
        using var otherAll = manager.TryCreateSession("WebSocket", null, new(tenant, schema))!;
        using var wrongDatastream = manager.TryCreateSession("SSE", 2, scope)!;

        manager.PublishObservations([Observation(42.5)], scope);

        Assert.True(own.Reader.TryRead(out var frame));
        AssertFrame(frame, 42.5);
        Assert.False(other.Reader.TryRead(out _));
        Assert.False(otherAll.Reader.TryRead(out _));
        Assert.False(wrongDatastream.Reader.TryRead(out _));
    }

    [UnitTest]
    public void ClusterBroadcast_MissingScope_IsRejectedAndUsesVersionedChannel()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetSubscriber().Returns(subscriber);
        Action<RedisChannel, RedisValue>? receive = null;
        RedisChannel channel = default;
        subscriber.When(s => s.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(call => { channel = call.ArgAt<RedisChannel>(0); receive = call.ArgAt<Action<RedisChannel, RedisValue>>(1); });
        using var manager = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance, redis);
        using var unscoped = manager.TryCreateSession("SSE", null, new(null, null))!;
        using var tenant = manager.TryCreateSession("SSE", 1, new("tenant-a", "schema_a"))!;
        Assert.NotNull(receive);
        Assert.Equal("sta:observation:stream:v2:broadcast", channel.ToString());

        // Old or malformed envelopes cannot turn the single-tenant partition into a wildcard.
        receive(channel, """{"OriginInstanceId":"old-node","Frame":{"@iot.id":49,"datastreamId":1,"result":42.5}}""");
        Assert.False(unscoped.Reader.TryRead(out _));
        Assert.False(tenant.Reader.TryRead(out _));

        var scope = new ObservationStreamScope(null, null);
        manager.PublishObservations([Observation(81.25)], scope);
        Assert.True(unscoped.Reader.TryRead(out var frame));
        AssertFrame(frame, 81.25);
        Assert.False(tenant.Reader.TryRead(out _));
    }

    internal static SensorThingsObservation Observation(double value) => new()
    {
        Id = 49,
        DatastreamId = 1,
        PhenomenonTime = DateTimeOffset.Parse("2026-09-05T01:02:03Z", System.Globalization.CultureInfo.InvariantCulture),
        Result = value
    };

    internal static void AssertFrame(ObservationStreamFrame frame, double expected)
    {
        Assert.Equal(49, frame.IotId);
        Assert.Equal(1, frame.DatastreamId);
        Assert.Equal(expected, frame.Result);
        Assert.Equal("2026-09-05T01:02:03.000Z", frame.PhenomenonTime);
        // Internal routing information is never part of the public observation payload.
        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
        Assert.DoesNotContain("Tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schema", json, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ObservationStreamRedisTenantIsolationTests : IAsyncLifetime
{
    private readonly RedisFixture _redis = new();

    public Task InitializeAsync() => _redis.InitializeAsync();
    public Task DisposeAsync() => _redis.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    public async Task PublishObservations_TwoNodesWithRedis_DeliversOnlyMatchingTenantAndSchema()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        using var first = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance, connection);
        using var second = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance, connection);
        var scopeA = new ObservationStreamScope("tenant-a", "schema_a");
        var scopeB = new ObservationStreamScope("tenant-b", "schema_b");
        using var local = first.TryCreateSession("SSE", 1, scopeA)!;
        using var remoteA = second.TryCreateSession("WebSocket", 1, scopeA)!;
        using var remoteB = second.TryCreateSession("SSE", null, scopeB)!;
        using var remoteWrongSchema = second.TryCreateSession("WebSocket", null, new("tenant-a", "schema_b"))!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        first.PublishObservations([ObservationStreamTenantIsolationTests.Observation(42.5)], scopeA);
        ObservationStreamTenantIsolationTests.AssertFrame(await local.Reader.ReadAsync(cts.Token), 42.5);
        ObservationStreamTenantIsolationTests.AssertFrame(await remoteA.Reader.ReadAsync(cts.Token), 42.5);
        first.PublishObservations([ObservationStreamTenantIsolationTests.Observation(81.25)], scopeB);
        ObservationStreamTenantIsolationTests.AssertFrame(await remoteB.Reader.ReadAsync(cts.Token), 81.25);

        Assert.False(local.Reader.TryRead(out _));
        Assert.False(remoteA.Reader.TryRead(out _));
        Assert.False(remoteB.Reader.TryRead(out _));
        Assert.False(remoteWrongSchema.Reader.TryRead(out _));
    }
}
