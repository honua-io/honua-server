// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Trait("Tier", "Fast")]
public sealed class RedisAdminApiKeyStoreConcurrencyTests
{
    [Theory]
    [InlineData("used", true)]
    [InlineData("permissions-reduced", true)]
    [InlineData("revoked", false)]
    [InlineData("rotated", false)]
    [InlineData("expired", false)]
    [InlineData("deleted", false)]
    public async Task ValidateAsync_ConcurrentRecordUpdate_RevalidatesCurrentAuthority(string change, bool allowed)
    {
        var now = DateTimeOffset.UtcNow;
        var original = Record(now);
        var current = change switch
        {
            "used" => original with { LastUsedAt = now.AddSeconds(-1), UpdatedAt = now.AddSeconds(-1) },
            "permissions-reduced" => original with { Permissions = ["admin:read"] },
            "revoked" => original with { RevokedAt = now },
            "rotated" => original with { KeyHash = SHA256.HashData("rotated-key"u8), RotatedAt = now },
            "expired" => original with { ExpiresAt = now },
            _ => null,
        };
        var (store, transaction) = Setup(original, current, now);

        var result = await store.ValidateAsync("valid-key", CancellationToken.None);

        Assert.Equal(allowed, result is not null);
        // A usage write already inside the one-minute window is not repeated, so the re-read
        // record is returned after the first compare-and-set loses.
        var throttled = change == "used";
        await transaction.Received(allowed ? (throttled ? 1 : 2) : 1).ExecuteAsync();
        if (allowed)
        {
            if (result is not { } validated)
            {
                throw new InvalidOperationException("validation result was missing");
            }

            Assert.Equal(original.Id, validated.Record.Id);
            Assert.Equal(throttled ? now.AddSeconds(-1) : now, validated.Record.LastUsedAt);
            Assert.Equal(current!.Permissions, validated.Record.Permissions);
        }
    }

    [Fact]
    public async Task ValidateAsync_UnknownKey_DoesNotFetchActiveRecords()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase().Returns(database);
        var activeIds = Enumerable.Range(0, 1_000)
            .Select(_ => (RedisValue)Guid.NewGuid().ToString("D"))
            .ToArray();
        database.SetMembersAsync(Arg.Any<RedisKey>()).Returns(activeIds);
        database.StringGetAsync(Arg.Any<RedisKey[]>()).Returns(call =>
            call.Arg<RedisKey[]>().Select(_ => RedisValue.Null).ToArray());
        database.SetCombineAsync(Arg.Any<SetOperation>(), Arg.Any<RedisKey[]>())
            .Returns(activeIds);

        var store = new RedisAdminApiKeyStore(redis, new FixedTimeProvider(DateTimeOffset.UtcNow));
        Assert.Null(await store.ValidateAsync("not-a-real-key", CancellationToken.None));

        await database.Received(1).StringGetAsync(Arg.Any<RedisKey>());
        await database.DidNotReceive().StringGetAsync(Arg.Any<RedisKey[]>());
        await database.DidNotReceive().SetMembersAsync(Arg.Any<RedisKey>());
        await database.DidNotReceive().SetCombineAsync(Arg.Any<SetOperation>(), Arg.Any<RedisKey[]>());
    }

    [Fact]
    public async Task ValidateAsync_RepeatedContention_BoundsRetriesAndFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var original = Record(now);
        var (store, transaction) = Setup(original, original, now);
        transaction.ExecuteAsync().Returns(false);

        Assert.Null(await store.ValidateAsync("valid-key", CancellationToken.None));

        await transaction.Received(3).ExecuteAsync();
    }

    private static AdminApiKeyRecord Record(DateTimeOffset now) => new(
        Guid.NewGuid(), "approver", "valid", SHA256.HashData("valid-key"u8), ["admin:*"],
        now.AddHours(-1), now.AddHours(-1), now.AddHours(1), null, null, null, "test");

    private static (RedisAdminApiKeyStore Store, ITransaction Transaction) Setup(
        AdminApiKeyRecord original, AdminApiKeyRecord? current, DateTimeOffset now)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        redis.GetDatabase().Returns(database);
        var hash = SHA256.HashData("valid-key"u8);
        database.StringGetAsync(RedisAdminApiKeyStore.HashIndexKey(hash))
            .Returns((RedisValue)original.Id.ToString("D"));
        database.StringGetAsync((RedisKey)$"honua:auth:admin-api-key:{original.Id:D}")
            .Returns(
                (RedisValue)JsonSerializer.Serialize(original),
                current is null ? RedisValue.Null : (RedisValue)JsonSerializer.Serialize(current));
        database.CreateTransaction().Returns(transaction);
        transaction.ExecuteAsync().Returns(false, true);
        return (new RedisAdminApiKeyStore(redis, new FixedTimeProvider(now)), transaction);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
