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
public sealed class RedisImportUploadStoreTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task Append_ConcurrentWriters_CommitExactlyOneChunkAndOffset()
    {
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var first = new RedisImportUploadStore(firstConnection);
        var second = new RedisImportUploadStore(secondConnection);
        var session = Session();
        (await first.TryCreateAsync(session)).Should().BeTrue();
        var a = Chunk(session, "first-object");
        var b = Chunk(session, "second-object");

        var accepted = await Task.WhenAll(first.TryAppendAsync(session, a), second.TryAppendAsync(session, b));

        accepted.Count(value => value).Should().Be(1);
        var restored = (await second.GetAsync(session.Id, session.Scope))!;
        restored.Version.Should().Be(2);
        restored.ChunkCount.Should().Be(1);
        restored.AcceptedBytes.Should().Be(session.ChunkBytes);
        (await second.ReadChunksAsync(session.Id, session.Scope, 0, 128))
            .Should().Equal(accepted[0] ? a : b);
        (await firstConnection.GetDatabase().HashLengthAsync(RedisImportUploadStore.Key(session.Id))).Should().Be(2);
    }

    [IntegrationTest]
    public async Task Append_LostReply_NewConnectionRecoversOriginalReceiptWithoutAcceptingAgain()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        var chunk = Chunk(session);
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(
                    call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Reply lost after upload receipt commit");
            });
        var uncertainConnection = Substitute.For<IConnectionMultiplexer>();
        uncertainConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var uncertainStore = new RedisImportUploadStore(uncertainConnection);

        await FluentActions.Invoking(() => uncertainStore.TryAppendAsync(session, chunk))
            .Should().ThrowAsync<RedisConnectionException>();

        await using var replacementConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var restarted = new RedisImportUploadStore(replacementConnection);
        var recovered = (await restarted.GetAsync(session.Id, session.Scope))!;
        recovered.AcceptedBytes.Should().Be(chunk.Bytes);
        recovered.IntentSha256.Should().Be(session.IntentSha256);
        (await restarted.ReadChunksAsync(session.Id, session.Scope, 0, 1)).Should().Equal(chunk);
        (await restarted.TryAppendAsync(session, chunk)).Should().BeFalse();
        (await restarted.GetAsync(session.Id, session.Scope))!.Version.Should().Be(recovered.Version);
    }

    [IntegrationTest]
    public async Task Scope_ChangedActorTenantTargetWorkspaceOrTask_CannotObserveOrMutateOriginal()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        ImportUploadScope[] wrongScopes =
        [
            session.Scope with { ActorId = "other-actor" },
            session.Scope with { TenantId = "other-tenant" },
            session.Scope with { TenantId = null },
            session.Scope with { TargetId = "other-target" },
            session.Scope with { WorkspaceId = "other-workspace" },
            session.Scope with { TaskId = "other-task" },
            session.Scope with { TaskRevision = 2 }
        ];
        foreach (var scope in wrongScopes)
        {
            (await store.GetAsync(session.Id, scope)).Should().BeNull();
            (await store.ReadChunksAsync(session.Id, scope, 0, 1)).Should().BeEmpty();
            (await store.TryCreateAsync(session with { Scope = scope })).Should().BeFalse();
            (await store.TryAppendAsync(session with { Scope = scope }, Chunk(session))).Should().BeFalse();
            (await store.TryCancelAsync(session with { Scope = scope })).Should().BeFalse();
        }

        (await store.TryAppendAsync(session with { IntentSha256 = new string('b', 64) }, Chunk(session))).Should().BeFalse();
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task Seal_RequiresEveryDeclaredByte_AndFreezesTheAcceptedManifest()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        var digest = new string('c', 64);
        (await store.TryCreateAsync(session)).Should().BeTrue();
        (await store.TrySealAsync(session, digest)).Should().BeFalse();
        var first = Chunk(session);
        (await store.TryAppendAsync(session, first)).Should().BeTrue();
        var next = (await store.GetAsync(session.Id, session.Scope))!;
        (await store.TrySealAsync(next, digest)).Should().BeFalse();
        var last = Chunk(next, "last-object");
        last.Bytes.Should().Be(17);
        (await store.TryAppendAsync(next, last)).Should().BeTrue();
        var complete = (await store.GetAsync(session.Id, session.Scope))!;
        (await store.TrySealAsync(complete, digest)).Should().BeTrue();

        var sealedSession = (await store.GetAsync(session.Id, session.Scope))!;
        sealedSession.State.Should().Be(ImportUploadState.Sealed);
        sealedSession.ContentSha256.Should().Be(digest);
        sealedSession.AcceptedBytes.Should().Be(session.TotalBytes);
        (await store.ReadChunksAsync(session.Id, session.Scope, 0, 128)).Should().Equal(first, last);
        (await store.TryAppendAsync(next, last)).Should().BeFalse();
        (await store.TryAppendAsync(sealedSession, last)).Should().BeFalse();
        (await store.TryCancelAsync(complete)).Should().BeFalse();
        (await store.TryCancelAsync(sealedSession)).Should().BeFalse();
        (await store.TrySealAsync(complete, new string('d', 64))).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Cancel_StopsLateChunkAcceptanceAndRetainsOnlyTheCancellationReceipt()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        (await store.TryCancelAsync(session)).Should().BeTrue();
        (await store.TryAppendAsync(session, Chunk(session))).Should().BeFalse();
        (await store.ReadChunksAsync(session.Id, session.Scope, 0, 128)).Should().BeEmpty();
        var cancelled = (await store.GetAsync(session.Id, session.Scope))!;
        cancelled.State.Should().Be(ImportUploadState.Cancelled);
        cancelled.AcceptedBytes.Should().Be(0);
        (await store.TryCreateAsync(session)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Expiry_ReceiptWritesNeverExtendLifetimeOrRecreateExpiredSession()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        var key = RedisImportUploadStore.Key(session.Id);
        var expiry = await database.KeyExpireTimeAsync(key);
        (await store.TryAppendAsync(session, Chunk(session))).Should().BeTrue();
        (await database.KeyExpireTimeAsync(key)).Should().Be(expiry);
        var current = (await store.GetAsync(session.Id, session.Scope))!;
        // Actual Redis expiry, without timing sleeps or changing application clocks.
        await database.KeyExpireAsync(key, TimeSpan.Zero);
        (await store.GetAsync(session.Id, session.Scope)).Should().BeNull();
        (await store.TryAppendAsync(current, Chunk(current))).Should().BeFalse();
        (await store.TryCancelAsync(current)).Should().BeFalse();
        (await database.KeyExistsAsync(key)).Should().BeFalse();
        (await store.TryCreateAsync(Session() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) })).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task InvalidRangesAndWrongRedisType_LeaveReceiptUnchanged()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        var chunk = Chunk(session);
        foreach (var invalid in new[] { chunk with { Index = 1 }, chunk with { Offset = 1 },
                     chunk with { Bytes = chunk.Bytes - 1 }, chunk with { Bytes = int.MaxValue },
                     chunk with { Sha256 = "unverified" } })
        {
            await FluentActions.Invoking(() => store.TryAppendAsync(session, invalid)).Should().ThrowAsync<ArgumentException>();
        }

        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
        var other = Session();
        await connection.GetDatabase().StringSetAsync(RedisImportUploadStore.Key(other.Id), "wrong-type");
        await FluentActions.Invoking(() => store.TryAppendAsync(other, Chunk(other))).Should().ThrowAsync<RedisServerException>();
        (await connection.GetDatabase().StringGetAsync(RedisImportUploadStore.Key(other.Id))).Should().Be((RedisValue)"wrong-type");
    }

    [IntegrationTest]
    public async Task ManifestPages_AreBoundedAndMultiGigabyteDeclarationIsNotTruncated()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        // This checks 64-bit metadata, not a multi-GB transfer or processing claim.
        var session = Session() with { TotalBytes = 9L * 1024 * 1024 * 1024, ChunkBytes = RedisImportUploadStore.MaximumChunkBytes };
        (await store.TryCreateAsync(session)).Should().BeTrue();
        (await store.GetAsync(session.Id, session.Scope))!.TotalBytes.Should().Be(session.TotalBytes);
        await FluentActions.Invoking(() => store.ReadChunksAsync(session.Id, session.Scope, 0, 129))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Scope = new ImportUploadScope("stored-key:owner", "tenant", "docker-target", "workspace", "import-task", 1),
        IntentSha256 = new string('a', 64),
        FileName = "parcels.gpkg",
        TotalBytes = 65536 + 17,
        ChunkBytes = 65536,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static ImportUploadChunk Chunk(ImportUploadSession session, string fileId = "unique-staged-object") => new(
        session.ChunkCount, session.AcceptedBytes, (int)Math.Min(session.ChunkBytes, session.TotalBytes - session.AcceptedBytes),
        new string('a', 64), fileId, "server-object-generation");
}
