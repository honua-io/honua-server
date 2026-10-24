// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class RedisMcpRequestStoreTests
{
    [UnitTest]
    public void RequestAuthorityKeys_ShareTheUnchangedSessionKeyClusterSlot()
    {
        Assert.Equal(12739, Slot("123456789")); // CRC16/XMODEM's standard check vector.
        var manager = new McpSessionManager();
        for (var i = 0; i < 128; i++)
        {
            var session = manager.CreateSession();
            Assert.DoesNotContain('{', session);
            Assert.DoesNotContain('}', session);
            var keys = RedisMcpSessionStore.RequestKeys(session);
            Assert.Equal("mcp:session:" + session, keys[0].ToString());
            var expected = Slot(keys[0].ToString());
            Assert.All(keys, key => Assert.Equal(expected, Slot(key.ToString())));
        }
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousAdmissionAndCompensationFailure_StillRefuseWorkAndBoundTheGenerationReservation(bool reservationFailure)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "ambiguous admission");
        var compensationFailure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "lost cleanup");
        var calls = 0;
        long quotaLifetime = 0;
        string? cleanupMember = null;
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                calls++;
                if (calls == 1)
                {
                    quotaLifetime = (long)call.ArgAt<RedisValue[]>(2)[2];
                    return reservationFailure
                        ? Task.FromException<RedisResult>(failure)
                        : Task.FromResult(RedisResult.Create((RedisValue)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + quotaLifetime)));
                }

                return Task.FromException<RedisResult>(failure);
            });
        database.SortedSetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                cleanupMember = call.ArgAt<RedisValue>(1).ToString();
                return Task.FromException<bool>(compensationFailure);
            });

        var store = new RedisMcpSessionStore(redis);
        const string generation = "0123456789abcdef0123456789abcdef";
        var observed = await Assert.ThrowsAsync<RedisConnectionException>(() => store.UpdateRequestAsync(
            "session", "principal", "typed-id", generation, McpRequestOperation.Register, CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.Equal((long)McpRequestBounds.Lifetime.TotalMilliseconds, quotaLifetime);
        Assert.Equal("session:typed-id:" + generation, cleanupMember);
        Assert.Equal(reservationFailure ? 1 : 2, calls);
    }

    [UnitTest]
    public async Task BraceBearingNoncanonicalSession_CannotChangeAtomicKeyRouting()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var store = new RedisMcpSessionStore(redis);
        Assert.Equal(McpRequestState.Missing, await store.UpdateRequestAsync(
            "forged{slot}", "principal", "id", "generation", McpRequestOperation.Register, CancellationToken.None));
        Assert.Empty(redis.ReceivedCalls());
    }

    // Independent Redis Cluster slot oracle: the runtime only constructs keys.
    // XMODEM CRC16 over the first nonempty {...} tag, or the whole key, modulo 16384.
    private static int Slot(string key)
    {
        var open = key.IndexOf('{');
        var close = open < 0 ? -1 : key.IndexOf('}', open + 1);
        if (open >= 0 && close > open + 1)
        {
            key = key[(open + 1)..close];
        }

        var crc = 0;
        foreach (var value in Encoding.UTF8.GetBytes(key))
        {
            crc ^= value << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1) & 0xffff;
            }
        }

        return crc % 16384;
    }
}
