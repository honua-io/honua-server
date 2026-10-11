// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Real-Redis proofs for the shared MCP session store (honua-release#531,
/// journey stage 1.5). Each "instance" is a <see cref="McpSessionManager"/> over
/// its own Redis connection, sharing nothing in-process, so the assertions hold
/// for replicas behind a non-sticky router such as AWS Lambda.
/// </summary>
/// <remarks>These store-level integration tests issue no HTTP requests and claim no endpoint coverage.</remarks>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.GetMetadata)]
public sealed class RedisMcpSessionStoreTests(RedisFixture redis)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    [IntegrationTest]
    public async Task SessionMintedOnA_IsServedByB_WithTheSameViewPrincipalAndCapabilities()
    {
        await using var connectionA = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var connectionB = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var a = NewInstance(connectionA);
        var b = NewInstance(connectionB);

        var id = await a.TryCreateSessionAsync("sub:alice", elicitationSupported: true, "setup", CancellationToken.None);
        Assert.NotNull(id);
        try
        {
            Assert.Equal(McpSessionValidation.Valid, await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None));
            Assert.Equal("setup", b.GetWorkflowView(id!));
            Assert.True(b.SupportsElicitation(id!));

            var otherInstance = NewInstance(connectionB);
            Assert.Equal(
                McpSessionValidation.PrincipalMismatch,
                await otherInstance.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None));
        }
        finally
        {
            await connectionA.GetDatabase().KeyDeleteAsync(RedisMcpSessionStore.Key(id!));
        }
    }

    [IntegrationTest]
    public async Task Record_HasTheIdleTimeoutAsTtl_AndRequestsOnAnyInstanceSlideIt()
    {
        await using var connectionA = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var connectionB = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var a = NewInstance(connectionA);
        var b = NewInstance(connectionB);
        var database = connectionA.GetDatabase();

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        try
        {
            var key = RedisMcpSessionStore.Key(id!);
            var ttl = await database.KeyTimeToLiveAsync(key);
            Assert.NotNull(ttl);
            Assert.InRange(ttl!.Value, IdleTimeout - TimeSpan.FromMinutes(1), IdleTimeout);

            // Shorten the TTL as if most of the idle window had elapsed, then serve a
            // request on B: the sliding window is restored for every instance.
            await database.KeyExpireAsync(key, TimeSpan.FromSeconds(30));
            Assert.Equal(McpSessionValidation.Valid, await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None));
            ttl = await database.KeyTimeToLiveAsync(key);
            Assert.InRange(ttl!.Value, IdleTimeout - TimeSpan.FromMinutes(1), IdleTimeout);

            // And again through A's cached copy.
            await database.KeyExpireAsync(key, TimeSpan.FromSeconds(30));
            Assert.Equal(McpSessionValidation.Valid, await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None));
            ttl = await database.KeyTimeToLiveAsync(key);
            Assert.InRange(ttl!.Value, IdleTimeout - TimeSpan.FromMinutes(1), IdleTimeout);
        }
        finally
        {
            await database.KeyDeleteAsync(RedisMcpSessionStore.Key(id!));
        }
    }

    [IntegrationTest]
    public async Task ExpiredOrDeletedRecord_IsUnknownOnEveryInstance()
    {
        await using var connectionA = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var connectionB = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var a = NewInstance(connectionA);
        var b = NewInstance(connectionB);

        // Deleted on B: A's cached copy is dropped on its next request.
        var deleted = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        Assert.Equal(McpSessionValidation.Valid, await b.ValidateAccessAsync(deleted!, "sub:alice", CancellationToken.None));
        var lifetimeOnA = a.GetLifetimeToken(deleted!);
        Assert.Equal(McpSessionTermination.Terminated, await b.TerminateAsync(deleted!, CancellationToken.None));
        // The pub/sub removal broadcast ends A's node-local session work promptly.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!lifetimeOnA.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(lifetimeOnA.IsCancellationRequested);
        Assert.False(await connectionA.GetDatabase().KeyExistsAsync(RedisMcpSessionStore.Key(deleted!)));
        Assert.Equal(McpSessionValidation.Unknown, await a.ValidateAccessAsync(deleted!, "sub:alice", CancellationToken.None));

        // Expired in Redis (the shared idle window lapsed): unknown everywhere.
        var expired = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        await connectionA.GetDatabase().KeyDeleteAsync(RedisMcpSessionStore.Key(expired!));
        Assert.Equal(McpSessionValidation.Unknown, await a.ValidateAccessAsync(expired!, "sub:alice", CancellationToken.None));
        Assert.Equal(McpSessionValidation.Unknown, await b.ValidateAccessAsync(expired!, "sub:alice", CancellationToken.None));
    }

    [IntegrationTest]
    public async Task Record_IsSourceGeneratedJson_WithoutNodeLocalState()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var a = NewInstance(connection);

        var id = await a.TryCreateSessionAsync(McpSessionManager.AnonymousPrincipalKey, false, null, CancellationToken.None);
        try
        {
            var payload = (string?)await connection.GetDatabase().StringGetAsync(RedisMcpSessionStore.Key(id!));
            Assert.NotNull(payload);
            Assert.Contains($"\"sessionId\":\"{id}\"", payload, StringComparison.Ordinal);
            Assert.Contains("\"principalKey\":\"\"", payload, StringComparison.Ordinal);
            Assert.Contains("\"schemaVersion\":1", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("channel", payload, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await connection.GetDatabase().KeyDeleteAsync(RedisMcpSessionStore.Key(id!));
        }
    }

    private static McpSessionManager NewInstance(IConnectionMultiplexer connection) =>
        new(idleTimeout: IdleTimeout, store: new RedisMcpSessionStore(connection));
}
