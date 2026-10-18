// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisImportStorageBudgetTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData("global")]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("count")]
    public async Task Reserve_ConcurrentReplicas_CannotOverrunAnyAggregateLimit(string limit)
    {
        await using var a = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var b = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var limits = limit switch
        {
            "global" => new ImportStorageBudgetLimits(100, 100, 100, 10),
            "tenant" => new ImportStorageBudgetLimits(1000, 100, 100, 10),
            "owner" => new ImportStorageBudgetLimits(1000, 1000, 100, 10),
            _ => new ImportStorageBudgetLimits(1000, 1000, 1000, 1)
        };
        var first = new RedisImportStorageBudget(a, domain, limits);
        var second = new RedisImportStorageBudget(b, domain, limits);
        var x = Reservation(60);
        var y = Reservation(60) with
        {
            Scope = x.Scope with { ActorId = limit == "owner" ? x.Scope.ActorId : "another-owner",
                TenantId = limit == "global" ? "another-tenant" : x.Scope.TenantId }
        };
        try
        {
            var results = await Task.WhenAll(first.TryReserveAsync(x), second.TryReserveAsync(y));

            results.Count(value => value).Should().Be(1);
            ((long)await a.GetDatabase().HashGetAsync(first.Key, "bytes")).Should().Be(60);
            ((long)await b.GetDatabase().HashGetAsync(second.Key, "count")).Should().Be(1);
            (await a.GetDatabase().KeyTimeToLiveAsync(first.Key)).Should().BeNull("expiry never proves storage deletion");
            var original = results[0] ? x : y;
            (await second.TryReserveAsync(original)).Should().BeTrue("reservation recovery does not charge twice");
            ((long)await b.GetDatabase().HashGetAsync(second.Key, "bytes")).Should().Be(60);
        }
        finally { await a.GetDatabase().KeyDeleteAsync(first.Key); }
    }

    [IntegrationTest]
    public async Task Reserve_LostCommitReply_RestartRecoversExactOriginalCharge()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var limits = new ImportStorageBudgetLimits(100, 100, 100, 1);
        var ordinary = new RedisImportStorageBudget(connection, domain, limits);
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost reservation reply after commit");
            });
        var interrupted = Substitute.For<IConnectionMultiplexer>();
        interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var reservation = Reservation(100);
        try
        {
            await FluentActions.Invoking(() => new RedisImportStorageBudget(interrupted, domain, limits).TryReserveAsync(reservation))
                .Should().ThrowAsync<RedisConnectionException>();
            await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var recovered = new RedisImportStorageBudget(restarted, domain, limits);
            (await recovered.TryReserveAsync(reservation)).Should().BeTrue();
            (await recovered.TryReserveAsync(Reservation(1))).Should().BeFalse();
            ((long)await restarted.GetDatabase().HashGetAsync(recovered.Key, "bytes")).Should().Be(100);
            await FluentActions.Invoking(() => recovered.TryReserveAsync(reservation with { Bytes = 99 }))
                .Should().ThrowAsync<InvalidOperationException>();
            await FluentActions.Invoking(() => recovered.ReleaseDeletedAsync(reservation with { Scope = reservation.Scope with { ActorId = "foreign" } }))
                .Should().ThrowAsync<InvalidOperationException>();
            ((long)await restarted.GetDatabase().HashGetAsync(recovered.Key, "bytes")).Should().Be(100);
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(ordinary.Key); }
    }

    [IntegrationTest]
    public async Task Release_ExactReservation_IsIdempotentAndFreesOnlyOriginalCharge()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = new RedisImportStorageBudget(connection, Guid.NewGuid().ToString("N"), new(100, 100, 100, 2));
        var first = Reservation(60);
        var second = Reservation(40);
        try
        {
            (await budget.TryReserveAsync(first)).Should().BeTrue();
            (await budget.TryReserveAsync(second)).Should().BeTrue();
            (await budget.ReleaseDeletedAsync(first)).Should().BeTrue();
            (await budget.ReleaseDeletedAsync(first)).Should().BeFalse();
            ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(40);
            ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "count")).Should().Be(1);
            (await budget.TryReserveAsync(Reservation(61))).Should().BeFalse();
            (await budget.TryReserveAsync(Reservation(60))).Should().BeTrue();
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(budget.Key); }
    }

    [IntegrationTheory]
    [InlineData("wrong-type")]
    [InlineData("invalid-counter")]
    [InlineData("missing-counter")]
    [InlineData("undercounted-counter")]
    [InlineData("expiring-ledger")]
    public async Task Reserve_CorruptOrExpiringAccounting_FailsWithoutAnyMutation(string corruption)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = new RedisImportStorageBudget(connection, Guid.NewGuid().ToString("N"), new(100, 100, 100, 2));
        var database = connection.GetDatabase();
        try
        {
            if (corruption == "wrong-type") await database.StringSetAsync(budget.Key, "foreign");
            else
            {
                (await budget.TryReserveAsync(Reservation(40))).Should().BeTrue();
                if (corruption == "invalid-counter") await database.HashSetAsync(budget.Key, "bytes", "NaN");
                if (corruption == "missing-counter") await database.HashDeleteAsync(budget.Key, "count");
                if (corruption == "undercounted-counter") await database.HashSetAsync(budget.Key, "bytes", 1);
                if (corruption == "expiring-ledger") await database.KeyExpireAsync(budget.Key, TimeSpan.FromHours(1));
            }
            var before = await database.KeyDumpAsync(budget.Key);

            await FluentActions.Invoking(() => budget.TryReserveAsync(Reservation(20))).Should().ThrowAsync<InvalidDataException>();

            (await database.KeyDumpAsync(budget.Key)).Should().Equal(before!);
        }
        finally { await database.KeyDeleteAsync(budget.Key); }
    }

    [IntegrationTest]
    public async Task Release_LostReply_RetryCannotSubtractAnotherObjectsCharge()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var limits = new ImportStorageBudgetLimits(100, 100, 100, 2);
        var budget = new RedisImportStorageBudget(connection, domain, limits);
        var first = Reservation(60);
        var second = Reservation(40);
        try
        {
            (await budget.TryReserveAsync(first)).Should().BeTrue();
            (await budget.TryReserveAsync(second)).Should().BeTrue();
            var database = Substitute.For<IDatabase>();
            database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
                .Returns(async call =>
                {
                    await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                    throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost release reply after commit");
                });
            var interrupted = Substitute.For<IConnectionMultiplexer>();
            interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
            await FluentActions.Invoking(() => new RedisImportStorageBudget(interrupted, domain, limits).ReleaseDeletedAsync(first))
                .Should().ThrowAsync<RedisConnectionException>();

            await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var recovered = new RedisImportStorageBudget(restarted, domain, limits);
            (await recovered.ReleaseDeletedAsync(first)).Should().BeFalse();
            (await recovered.TryReserveAsync(second)).Should().BeTrue();
            ((long)await restarted.GetDatabase().HashGetAsync(recovered.Key, "bytes")).Should().Be(40);
            ((long)await restarted.GetDatabase().HashGetAsync(recovered.Key, "count")).Should().Be(1);
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(budget.Key); }
    }

    [IntegrationTest]
    public async Task Reserve_LargeIntegerAccounting_PreservesExactBytesAcrossRecoveryAndRelease()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var maximum = RedisImportStorageBudget.MaximumBudgetBytes;
        var budget = new RedisImportStorageBudget(connection, Guid.NewGuid().ToString("N"), new(maximum, maximum, maximum, 10));
        var bytes = 5L * 1024 * 1024 * 1024 + 17;
        var reservation = Reservation(bytes);
        try
        {
            (await budget.TryReserveAsync(reservation)).Should().BeTrue();
            (await budget.TryReserveAsync(reservation)).Should().BeTrue();
            ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(bytes);
            (await budget.ReleaseDeletedAsync(reservation)).Should().BeTrue();
            ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(0);
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(budget.Key); }
        // This verifies integer accounting, not a multi-GB upload or disk allocation.
    }

    [IntegrationTest]
    public async Task Reserve_DivergentReplicaPolicy_CannotRaiseBudgetButCanRecoverAndReleaseOriginal()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var original = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 1));
        var changed = new RedisImportStorageBudget(connection, domain, new(1000, 1000, 1000, 10));
        var reservation = Reservation(100);
        try
        {
            (await original.TryReserveAsync(reservation)).Should().BeTrue();

            await FluentActions.Invoking(() => changed.TryReserveAsync(Reservation(1))).Should().ThrowAsync<InvalidOperationException>();

            (await changed.TryReserveAsync(reservation)).Should().BeTrue();
            ((long)await connection.GetDatabase().HashGetAsync(original.Key, "bytes")).Should().Be(100);
            (await changed.ReleaseDeletedAsync(reservation)).Should().BeTrue();
            ((long)await connection.GetDatabase().HashGetAsync(original.Key, "bytes")).Should().Be(0);
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(original.Key); }
    }

    private static ImportStorageReservation Reservation(long bytes)
    {
        var session = new ImportUploadSession
        {
            Id = Guid.NewGuid().ToString("N"), Scope = new("owner", "tenant", "target", "workspace", "task", 1),
            IntentSha256 = new string('a', 64), FileName = "parcels.gpkg", TotalBytes = bytes,
            ChunkBytes = RedisImportUploadStore.MaximumChunkBytes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        return RedisImportStorageBudget.ForObject(session, $"imports/resumable/{session.Id}/source-{Guid.NewGuid():N}", bytes);
    }
}
