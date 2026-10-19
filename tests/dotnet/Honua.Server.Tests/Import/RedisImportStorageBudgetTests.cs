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
        var first = ImportStorageBudgetTestFactory.Bind(a, domain, limits);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(first);
        var second = ImportStorageBudgetTestFactory.Bind(b, domain, limits);
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
        var ordinary = ImportStorageBudgetTestFactory.Bind(connection, domain, limits);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(ordinary);
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
            await FluentActions.Invoking(() => ImportStorageBudgetTestFactory.Bind(interrupted, domain, limits).TryReserveAsync(reservation))
                .Should().ThrowAsync<RedisConnectionException>();
            await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var recovered = ImportStorageBudgetTestFactory.Bind(restarted, domain, limits);
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
        var budget = ImportStorageBudgetTestFactory.Bind(connection, Guid.NewGuid().ToString("N"), new(100, 100, 100, 2));
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
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
        var budget = ImportStorageBudgetTestFactory.Bind(connection, Guid.NewGuid().ToString("N"), new(100, 100, 100, 2));
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
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
        var budget = ImportStorageBudgetTestFactory.Bind(connection, domain, limits);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
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
            await FluentActions.Invoking(() => ImportStorageBudgetTestFactory.Bind(interrupted, domain, limits).ReleaseDeletedAsync(first))
                .Should().ThrowAsync<RedisConnectionException>();

            await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var recovered = ImportStorageBudgetTestFactory.Bind(restarted, domain, limits);
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
        var budget = ImportStorageBudgetTestFactory.Bind(connection, Guid.NewGuid().ToString("N"), new(maximum, maximum, maximum, 10));
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
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
        var original = ImportStorageBudgetTestFactory.Bind(connection, domain, new(100, 100, 100, 1));
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(original);
        var changed = ImportStorageBudgetTestFactory.Bind(connection, domain, new(1000, 1000, 1000, 10));
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

    [IntegrationTest]
    public async Task MissingLedgerRefusesAdmissionAndReleaseWithoutCreatingEmptyAccounting()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = ImportStorageBudgetTestFactory.Bind(connection, Guid.NewGuid().ToString("N"), new(100, 100, 100, 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => budget.TryReserveAsync(Reservation(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => budget.ReleaseDeletedAsync(Reservation(1)));
        Assert.False(await connection.GetDatabase().KeyExistsAsync(budget.Key));
    }

    [IntegrationTest]
    public async Task RebuildAfterRedisLossRetainsCensusChargesAndRejectsEveryOldEpochMutation()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(100, 100, 100, 2));
        var first = Reservation(60);
        var second = Reservation(40);
        var recovered = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 2), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(await old.TryReserveAsync(first));
            Assert.True(await old.TryReserveAsync(second));
            await connection.GetDatabase().KeyDeleteAsync(old.Key);
            await Assert.ThrowsAsync<InvalidDataException>(() => old.TryReserveAsync(Reservation(1)));
            // Controlled census records: this is real Redis recovery, not a disk census test.
            await recovered.RebuildFromCensusAsync(null, Census(first, second));
            Assert.Equal(recovered.Epoch, await recovered.ReadStoredEpochAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => old.ReleaseDeletedAsync(first));
            await Assert.ThrowsAsync<InvalidOperationException>(() => old.TryReserveAsync(first));
            Assert.Equal(100, (long)await connection.GetDatabase().HashGetAsync(old.Key, "bytes"));
            Assert.True(await recovered.ReleaseDeletedAsync(first));
            Assert.Equal(40, (long)await connection.GetDatabase().HashGetAsync(old.Key, "bytes"));
            Assert.True(await recovered.TryReserveAsync(second));
            Assert.Null(await connection.GetDatabase().KeyTimeToLiveAsync(old.Key));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(old.Key); }
    }

    [IntegrationTest]
    public async Task RebuildUnderLowerLimitsPreservesExistingUsageAndDeniesNewWritesUntilItFits()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(100, 100, 100, 10));
        var reservation = Reservation(80);
        var reduced = new RedisImportStorageBudget(connection, domain, new(50, 50, 50, 1), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(await old.TryReserveAsync(reservation));
            await reduced.RebuildFromCensusAsync(old.Epoch, Census(reservation));
            Assert.Equal(80, (long)await connection.GetDatabase().HashGetAsync(old.Key, "bytes"));
            Assert.True(await reduced.TryReserveAsync(reservation));
            Assert.False(await reduced.TryReserveAsync(Reservation(1)));
            Assert.True(await reduced.ReleaseDeletedAsync(reservation));
            Assert.True(await reduced.TryReserveAsync(Reservation(50)));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(old.Key); }
    }

    [IntegrationTest]
    public async Task PartialCensusFailureNeverPublishesItsIncompleteAccounting()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(1000, 1000, 1000, 100));
        var replacement = new RedisImportStorageBudget(connection, domain, new(1000, 1000, 1000, 100), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(await old.TryReserveAsync(Reservation(40)));
            var before = await connection.GetDatabase().KeyDumpAsync(old.Key);
            await Assert.ThrowsAsync<IOException>(() => replacement.RebuildFromCensusAsync(old.Epoch, InterruptedCensus()));
            Assert.Equal(before, await connection.GetDatabase().KeyDumpAsync(old.Key));
            Assert.Equal(old.Epoch, await old.ReadStoredEpochAsync());
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(old.Key); }

        static async IAsyncEnumerable<ImportStorageReservation> InterruptedCensus()
        {
            await Task.Yield();
            for (var index = 0; index < 65; index++) yield return Reservation(1);
            throw new IOException("Census interrupted after an actual staged batch.");
        }
    }

    [IntegrationTest]
    public async Task DelayedEarlierRecoveryCannotReplaceANewerPublishedEpoch()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(100, 100, 100, 2));
        var slow = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 2), Guid.NewGuid().ToString("N"));
        var winner = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 2), Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = Reservation(40);
        Task? delayed = null;
        try
        {
            Assert.True(await old.TryReserveAsync(reservation));
            var pending = slow.RebuildFromCensusAsync(old.Epoch, PausedCensus());
            delayed = pending;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await winner.RebuildFromCensusAsync(old.Epoch, Census(reservation));
            var published = await connection.GetDatabase().KeyDumpAsync(old.Key);
            resume.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal(published, await connection.GetDatabase().KeyDumpAsync(old.Key));
            Assert.Equal(winner.Epoch, await winner.ReadStoredEpochAsync());
        }
        finally
        {
            resume.TrySetResult();
            if (delayed is not null)
            {
                try { await delayed; }
                catch (InvalidOperationException) { }
            }
            await connection.GetDatabase().KeyDeleteAsync(old.Key);
        }

        async IAsyncEnumerable<ImportStorageReservation> PausedCensus()
        {
            yield return reservation;
            entered.TrySetResult();
            await resume.Task;
        }
    }

    [IntegrationTest]
    public async Task DuplicateCensusIdentityCannotReplaceTheActiveLedger()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(100, 100, 100, 2));
        var replacement = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 2), Guid.NewGuid().ToString("N"));
        try
        {
            var before = await connection.GetDatabase().KeyDumpAsync(old.Key);
            var duplicate = Reservation(10);
            await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.RebuildFromCensusAsync(old.Epoch, Census(duplicate, duplicate)));
            Assert.Equal(before, await connection.GetDatabase().KeyDumpAsync(old.Key));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(old.Key); }
    }

    [IntegrationTest]
    public async Task RebuildLostPublicationReplyRetainsPublishedEpochAndCharges()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var domain = Guid.NewGuid().ToString("N");
        var old = await ImportStorageBudgetTestFactory.CreateAsync(connection, domain, new(100, 100, 100, 2));
        var epoch = Guid.NewGuid().ToString("N");
        var reservation = Reservation(60);
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                var keys = call.ArgAt<RedisKey[]>(1);
                var result = await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0), keys, call.ArgAt<RedisValue[]>(2));
                if (keys.Length == 2) throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Publication reply lost after rename");
                return result;
            });
        var interrupted = Substitute.For<IConnectionMultiplexer>();
        interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var replacement = new RedisImportStorageBudget(interrupted, domain, new(100, 100, 100, 2), epoch);
        try
        {
            await Assert.ThrowsAsync<RedisConnectionException>(() => replacement.RebuildFromCensusAsync(old.Epoch, Census(reservation)));
            var observed = new RedisImportStorageBudget(connection, domain, new(100, 100, 100, 2), epoch);
            Assert.Equal(epoch, await observed.ReadStoredEpochAsync());
            Assert.Equal(60, (long)await connection.GetDatabase().HashGetAsync(old.Key, "bytes"));
            Assert.True(await observed.TryReserveAsync(reservation));
            await Assert.ThrowsAsync<InvalidOperationException>(() => old.ReleaseDeletedAsync(reservation));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(old.Key); }
    }

    private static async IAsyncEnumerable<ImportStorageReservation> Census(params ImportStorageReservation[] reservations)
    {
        await Task.Yield();
        foreach (var reservation in reservations) yield return reservation;
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
