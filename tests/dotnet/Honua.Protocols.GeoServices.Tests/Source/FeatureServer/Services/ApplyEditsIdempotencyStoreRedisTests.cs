// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Exercises <see cref="DistributedApplyEditsIdempotencyStore"/> against a real Redis server so the
/// distributed reservation path — the <c>SET NX</c> reserve and the compare-and-delete Lua release —
/// is actually executed. Every other suite for this class constructs it with
/// <c>MemoryDistributedCache</c> or <c>cache: null</c>, which routes to the in-process
/// <c>ConcurrentDictionary</c> fallback and therefore proves at-most-once only within one process;
/// at-most-once <em>across nodes</em> is the multi-node promise (#2250/#3052) and is what these
/// tests cover (honua-server#4406).
/// </summary>
/// <remarks>
/// Each test builds two store instances over the same Redis server. They stand in for two server
/// replicas: nothing is shared between them in-process, so every assertion below is a statement
/// about the Redis key, not about a shared dictionary.
/// </remarks>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.ApplyEdits)]
public sealed class ApplyEditsIdempotencyStoreRedisTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task TryReserveAsync_AcrossTwoNodesWithSameKey_ExactlyOneWins()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var scope = NewScope();

        var first = await nodeA.TryReserveAsync(scope);
        var second = await nodeB.TryReserveAsync(scope);

        first.Should().NotBeNull("the first node's SET NX must create the key");
        second.Should().BeNull(
            "the second node must lose the SET NX race and answer 409 rather than executing the " +
            "same edit a second time");
    }

    [IntegrationTest]
    public async Task TryReserveAsync_TenConcurrentCallersAcrossNodes_ExactlyOneWins()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodes = Enumerable.Range(0, 10).Select(_ => CreateStore(multiplexer)).ToArray();
        var scope = NewScope();

        var tokens = await Task.WhenAll(nodes.Select(node => node.TryReserveAsync(scope)));

        tokens.Count(token => token is not null).Should().Be(
            1,
            "SET NX is atomic in Redis, so exactly one of ten replicas may proceed to write");
        tokens.Where(token => token is not null).Distinct(StringComparer.Ordinal).Should().HaveCount(
            1,
            "the single winner's token must be unique to its reservation");
    }

    [IntegrationTest]
    public async Task ReleaseAsync_WithTheOwningToken_FreesTheKeyForAnotherNode()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var scope = NewScope();

        var token = await nodeA.TryReserveAsync(scope);
        token.Should().NotBeNull();
        (await nodeB.TryReserveAsync(scope)).Should().BeNull();

        // #3052: an edit that provably wrote nothing releases its reservation so a genuine retry —
        // here arriving at a different replica — is a fresh attempt, not a 409.
        await nodeA.ReleaseAsync(scope, token!);

        (await nodeB.TryReserveAsync(scope)).Should().NotBeNull(
            "the released key must be re-reservable by another node");
    }

    [IntegrationTest]
    public async Task ReleaseAsync_WithALapsedToken_LeavesTheCurrentOwnersReservation()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var nodeC = CreateStore(multiplexer);
        var scope = NewScope();

        var lapsed = await nodeA.TryReserveAsync(scope);
        lapsed.Should().NotBeNull();
        await nodeA.ReleaseAsync(scope, lapsed!);
        var current = await nodeB.TryReserveAsync(scope);
        current.Should().NotBeNull("node B re-reserved the key after node A's window lapsed");

        // The late release from node A must be a no-op: the Lua compare-and-delete only removes the
        // key while it still holds the caller's own token. Deleting node B's reservation would let a
        // third node execute the same edit alongside it.
        await nodeA.ReleaseAsync(scope, lapsed!);

        (await nodeC.TryReserveAsync(scope)).Should().BeNull(
            "a late release must not discard the reservation that now belongs to another request");
    }

    [IntegrationTest]
    public async Task ReleaseAsync_AfterTheResponseWasRecorded_LeavesTheRecordedResponse()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var scope = NewScope();

        var token = await nodeA.TryReserveAsync(scope);
        token.Should().NotBeNull();
        await nodeA.SetAsync(scope, ResponseWithObjectId(4406));

        // A stale release arriving after the response landed must not erase the replay value, or a
        // client retry would re-apply an edit that already committed.
        await nodeA.ReleaseAsync(scope, token!);

        var replayed = await nodeB.TryGetAsync(scope);
        replayed.Should().NotBeNull("the recorded response must survive a stale release");
        replayed!.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(
            4406,
            "the replay must return the original response, including the original object id");
    }

    [IntegrationTest]
    public async Task TryGetAsync_WhileAnotherNodeHoldsTheReservation_ReturnsNull()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var scope = NewScope();

        (await nodeA.TryReserveAsync(scope)).Should().NotBeNull();

        (await nodeB.TryGetAsync(scope)).Should().BeNull(
            "a pending reservation is not a replayable response, so it must never be deserialized " +
            "as one");
    }

    [IntegrationTest]
    public async Task SetAsync_ThenTryGetAsync_ReplaysTheResponseToAnotherNode()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var nodeA = CreateStore(multiplexer);
        var nodeB = CreateStore(multiplexer);
        var scope = NewScope();

        await nodeA.TryReserveAsync(scope);
        await nodeA.SetAsync(scope, ResponseWithObjectId(2250));

        var replayed = await nodeB.TryGetAsync(scope);

        replayed.Should().NotBeNull();
        replayed!.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(2250);
        replayed.AddResults![0].Success.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Scopes_DifferingOnlyByPrincipalOrLayer_DoNotShareAReservation()
    {
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = CreateStore(multiplexer);
        var key = Guid.NewGuid().ToString("n");

        (await store.TryReserveAsync(new ApplyEditsIdempotencyScope("svc", 0, "alice", key)))
            .Should().NotBeNull();
        (await store.TryReserveAsync(new ApplyEditsIdempotencyScope("svc", 0, "bob", key)))
            .Should().NotBeNull("one caller's key must never collide with another principal's");
        (await store.TryReserveAsync(new ApplyEditsIdempotencyScope("svc", 1, "alice", key)))
            .Should().NotBeNull("the same key on a different layer is a different edit");
        (await store.TryReserveAsync(new ApplyEditsIdempotencyScope("other", 0, "alice", key)))
            .Should().NotBeNull("the same key on a different service is a different edit");
        (await store.TryReserveAsync(new ApplyEditsIdempotencyScope("svc", 0, "alice", key)))
            .Should().BeNull("the identical scope must still be deduplicated");
    }

    private static DistributedApplyEditsIdempotencyStore CreateStore(IConnectionMultiplexer multiplexer)
        => new(multiplexer, cache: null, NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);

    private static ApplyEditsIdempotencyScope NewScope()
        => new("svc", 0, "alice", Guid.NewGuid().ToString("n"));

    private static ApplyEditsResponse ResponseWithObjectId(long objectId)
        => new()
        {
            AddResults = [new EditResult { ObjectId = objectId, Success = true }]
        };
}
