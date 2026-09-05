// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
            "revoked" => original with { RevokedAt = now },
            "rotated" => original with { KeyHash = SHA256.HashData("rotated-key"u8), RotatedAt = now },
            "expired" => original with { ExpiresAt = now },
            _ => null,
        };
        var (store, transaction) = Setup(original, current, now);

        var result = await store.ValidateAsync("valid-key", CancellationToken.None);

        Assert.Equal(allowed, result is not null);
        await transaction.Received(allowed ? 2 : 1).ExecuteAsync();
        if (allowed)
        {
            Assert.Equal(original.Id, result!.Record.Id);
            Assert.Equal(now, result.Record.LastUsedAt);
        }
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
        database.SetMembersAsync(Arg.Any<RedisKey>())
            .Returns(new RedisValue[] { original.Id.ToString("D") });
        database.StringGetAsync(Arg.Any<RedisKey[]>())
            .Returns(new RedisValue[] { JsonSerializer.Serialize(original) });
        database.StringGetAsync(Arg.Any<RedisKey>())
            .Returns(current is null ? RedisValue.Null : (RedisValue)JsonSerializer.Serialize(current));
        database.CreateTransaction().Returns(transaction);
        transaction.ExecuteAsync().Returns(false, true);
        return (new RedisAdminApiKeyStore(redis, new FixedTimeProvider(now)), transaction);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
