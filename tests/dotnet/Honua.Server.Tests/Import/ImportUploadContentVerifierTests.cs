// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ImportUploadContentVerifierTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task Verify_AfterRestartAcrossManifestPages_HashesActualOrderedFileWithoutMutatingSession()
    {
        using var disk = new StorageFixture();
        await using var originalConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var originalStore = new RedisImportUploadStore(originalConnection);
        var (session, digest) = await StageAsync(originalStore, disk.Storage, fullChunks: 129);
        var key = RedisImportUploadStore.Key(session.Id);
        var originalExpiry = await originalConnection.GetDatabase().KeyExpireTimeAsync(key);

        await using var restartedConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var restartedStore = new RedisImportUploadStore(restartedConnection);
        var restartedStorage = disk.OpenStorage();
        var verifier = new ImportUploadContentVerifier(restartedStore, new ImportUploadChunkStorage(restartedStorage));

        var verified = await verifier.VerifyAsync(session, digest);

        verified.Session.Should().Be(session);
        verified.Sha256.Should().Be(digest);
        (await restartedStore.GetAsync(session.Id, session.Scope)).Should().Be(session);
        (await restartedConnection.GetDatabase().KeyExpireTimeAsync(key)).Should().Be(originalExpiry);
        // Verification is read-only. Sealing is a separate coordinator CAS after
        // current authorization, and still does not accept or dispatch an import.
        (await restartedStore.TrySealAsync(verified.Session, verified.Sha256)).Should().BeTrue();
        var sealedSession = (await restartedStore.GetAsync(session.Id, session.Scope))!;
        sealedSession.ContentSha256.Should().Be(digest);
        sealedSession.State.Should().Be(ImportUploadState.Sealed);
    }

    [IntegrationTest]
    public async Task Verify_WrongWholeFileDigest_LeavesCompletedTransferUnsealed()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, _) = await StageAsync(store, disk.Storage);
        var verifier = new ImportUploadContentVerifier(store, new ImportUploadChunkStorage(disk.Storage));

        await FluentActions.Invoking(() => verifier.VerifyAsync(session, new string('0', 64)))
            .Should().ThrowAsync<InvalidDataException>();

        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task Verify_StoredChunkCorrupted_RejectsEvenWhenManifestAndExpectedDigestMatch()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        var last = (await store.ReadChunksAsync(session.Id, session.Scope, session.ChunkCount - 1, 1)).Single();
        var metadata = (await disk.Storage.GetMetadataAsync(last.FileId))!;
        await File.WriteAllBytesAsync(Path.Join(disk.Root, metadata.StoragePath), new byte[last.Bytes]);
        var verifier = new ImportUploadContentVerifier(store, new ImportUploadChunkStorage(disk.Storage));

        await FluentActions.Invoking(() => verifier.VerifyAsync(session, digest)).Should().ThrowAsync<InvalidDataException>();

        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task Verify_ChangedActorOrIntent_DoesNotOpenAnyChunk()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        var inaccessibleStorage = Substitute.For<ICloudFileStorage>();
        var verifier = new ImportUploadContentVerifier(store, new ImportUploadChunkStorage(inaccessibleStorage));

        await FluentActions.Invoking(() => verifier.VerifyAsync(session with
        {
            Scope = session.Scope with { ActorId = "another-actor" }
        }, digest)).Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Invoking(() => verifier.VerifyAsync(session with
        {
            IntentSha256 = new string('b', 64)
        }, digest)).Should().ThrowAsync<InvalidDataException>();

        inaccessibleStorage.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Scratch_MissingAggregateBudget_DoesNotReadChunksOrAllocateScratch()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var unavailable = Substitute.For<ICloudFileStorage>();
        var directory = Path.Join(disk.Root, "scratch");

        await FluentActions.Invoking(() => new ImportUploadScratch(store, new ImportUploadChunkStorage(unavailable))
            .OpenAsync(session, directory, session.TotalBytes, static _ => Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();

        unavailable.ReceivedCalls().Should().BeEmpty();
        Directory.Exists(directory).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_CancellationOrExpiryDuringFinalChunk_DoesNotReturnVerification(bool expire)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        var observedStorage = Substitute.For<ICloudFileStorage>();
        observedStorage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.GetMetadataAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observedStorage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var content = await disk.Storage.DownloadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
                if (expire)
                {
                    (await connection.GetDatabase().KeyExpireAsync(RedisImportUploadStore.Key(session.Id), TimeSpan.Zero))
                        .Should().BeTrue();
                }
                else
                {
                    (await store.TryCancelAsync(session)).Should().BeTrue();
                }

                return content;
            });
        var verifier = new ImportUploadContentVerifier(store, new ImportUploadChunkStorage(observedStorage));

        await FluentActions.Invoking(() => verifier.VerifyAsync(session, digest)).Should().ThrowAsync<InvalidDataException>();

        var current = await store.GetAsync(session.Id, session.Scope);
        if (expire)
        {
            current.Should().BeNull();
        }
        else
        {
            current!.State.Should().Be(ImportUploadState.Cancelled);
            current.ContentSha256.Should().BeNull();
        }
    }

    [IntegrationTest]
    public async Task Verify_MissingAcceptedManifestEntry_RefusesIncompleteContent()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        (await connection.GetDatabase().HashDeleteAsync(RedisImportUploadStore.Key(session.Id), "chunk:1"))
            .Should().BeTrue();
        var verifier = new ImportUploadContentVerifier(store, new ImportUploadChunkStorage(disk.Storage));

        await FluentActions.Invoking(() => verifier.VerifyAsync(session, digest)).Should().ThrowAsync<InvalidDataException>();
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task Scratch_AfterRestart_ReturnsVerifiedSeekableHandleAndDeletesItOnDispose()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 129);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        var sealedSession = (await store.GetAsync(session.Id, session.Scope))!;
        var expiry = await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id));
        await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var scratchDirectory = Path.Join(disk.Root, "scratch");
        var materializer = new ImportUploadScratch(new RedisImportUploadStore(restarted),
            new ImportUploadChunkStorage(disk.OpenStorage()), Scratch(disk.Root));
        var authorizations = 0;
        await using (var lease = await materializer.OpenAsync(sealedSession, scratchDirectory, sealedSession.TotalBytes, _ =>
        {
            authorizations++;
            return Task.CompletedTask;
        }))
        {
            var file = lease.Content;
            file.CanSeek.Should().BeTrue();
            file.Position.Should().Be(0);
            file.Length.Should().Be(sealedSession.TotalBytes);
            Convert.ToHexStringLower(await SHA256.HashDataAsync(file)).Should().Be(digest);
            file.Position = 65536;
            file.ReadByte().Should().Be((65536 + 1) % 251);
        }

        Directory.EnumerateFiles(scratchDirectory).Should().BeEmpty();
        authorizations.Should().BeGreaterThan(sealedSession.ChunkCount);
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(sealedSession);
        (await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id))).Should().Be(expiry);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scratch_UnsealedOrExceedsAllowance_DoesNotReadChunksOrAllocateScratch(bool seal)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        if (seal)
        {
            (await store.TrySealAsync(session, digest)).Should().BeTrue();
            session = (await store.GetAsync(session.Id, session.Scope))!;
        }

        var unavailable = Substitute.For<ICloudFileStorage>();
        var materializer = new ImportUploadScratch(store, new ImportUploadChunkStorage(unavailable), Scratch(disk.Root));
        var scratchDirectory = Path.Join(disk.Root, "scratch");
        await FluentActions.Invoking(() => materializer.OpenAsync(session, scratchDirectory,
            seal ? session.TotalBytes - 1 : session.TotalBytes, static _ => Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();

        unavailable.ReceivedCalls().Should().BeEmpty();
        Directory.Exists(scratchDirectory).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scratch_WrongFullDigestOrCorruptChunk_RemovesPartialFile(bool corruptChunk)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        (await store.TrySealAsync(session, corruptChunk ? digest : new string('0', 64))).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        if (corruptChunk)
        {
            var chunk = (await store.ReadChunksAsync(session.Id, session.Scope, session.ChunkCount - 1, 1)).Single();
            var metadata = (await disk.Storage.GetMetadataAsync(chunk.FileId))!;
            await File.WriteAllBytesAsync(Path.Join(disk.Root, metadata.StoragePath), new byte[chunk.Bytes]);
        }

        var scratchDirectory = Path.Join(disk.Root, "scratch");
        var materializer = new ImportUploadScratch(store, new ImportUploadChunkStorage(disk.Storage), Scratch(disk.Root));
        await FluentActions.Invoking(() => materializer.OpenAsync(session, scratchDirectory,
            session.TotalBytes, static _ => Task.CompletedTask)).Should().ThrowAsync<InvalidDataException>();

        Directory.EnumerateFiles(scratchDirectory).Should().BeEmpty();
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scratch_RevokedOrCancelledDuringProviderRead_DoesNotExposePartialFile(bool cancel)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        using var cancellation = new CancellationTokenSource();
        var revoked = false;
        var observed = Substitute.For<ICloudFileStorage>();
        observed.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.GetMetadataAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var stream = await disk.Storage.DownloadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
            if (cancel) await cancellation.CancelAsync();
            else revoked = true;
            return stream;
        });
        var scratchDirectory = Path.Join(disk.Root, "scratch");
        var materializer = new ImportUploadScratch(store, new ImportUploadChunkStorage(observed), Scratch(disk.Root));
        var action = () => materializer.OpenAsync(session, scratchDirectory, session.TotalBytes, _ =>
        {
            if (revoked) throw new UnauthorizedAccessException("Revoked during source read");
            return Task.CompletedTask;
        }, cancellation.Token);
        if (cancel) await FluentActions.Invoking(action).Should().ThrowAsync<OperationCanceledException>();
        else await FluentActions.Invoking(action).Should().ThrowAsync<UnauthorizedAccessException>();

        Directory.EnumerateFiles(scratchDirectory).Should().BeEmpty();
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task RetainedSource_SurvivesTransferReceiptExpiry_AndReopensExactBytesAfterRestart()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var originalExpiry = await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id));
        var publisher = new ImportUploadSourceStorage(store, disk.Storage, AllowStorageBudget(), Scratch(disk.Root));
        var source = await publisher.StageAsync(session, Path.Join(disk.Root, "scratch"), session.TotalBytes,
            static _ => Task.CompletedTask);
        source.ExpiresAt.Should().BeAfter(session.ExpiresAt);
        (await store.TryBindSourceAsync(session, source)).Should().BeTrue();
        await using var restart = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var restartedStore = new RedisImportUploadStore(restart);
        var bound = (await restartedStore.GetAsync(session.Id, session.Scope))!;
        bound.StagedSource.Should().Be(source);
        (await restart.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id))).Should().Be(originalExpiry);
        (await restartedStore.TryBindSourceAsync(bound, source)).Should().BeFalse("a bound source is immutable");
        Directory.EnumerateFiles(Path.Join(disk.Root, "scratch")).Should().BeEmpty();

        // Expire the transfer receipt; the retained job source is independently readable.
        (await restart.GetDatabase().KeyExpireAsync(RedisImportUploadStore.Key(session.Id), TimeSpan.Zero)).Should().BeTrue();
        (await restartedStore.GetAsync(session.Id, session.Scope)).Should().BeNull();
        var workerBudget = await ImportStorageBudgetTestFactory.CreateAsync(restart, disk.Root + ":worker-scratch",
            new ImportStorageBudgetLimits(session.TotalBytes, session.TotalBytes, session.TotalBytes, 10));
        await using (var input = await Honua.Import.FileImport.Durable.StagedImportSourceMaterializer.OpenAsync(
            bound.StagedSource!, disk.OpenStorage(), session.TotalBytes, new ImportScratchStorage(Path.Join(disk.Root, "worker-scratch"), workerBudget)))
        {
            Convert.ToHexStringLower(await SHA256.HashDataAsync(input.Content)).Should().Be(digest);
            ((long)await restart.GetDatabase().HashGetAsync(workerBudget.Key, "bytes")).Should().Be(session.TotalBytes);
            bound.StagedSource!.Transfer!.OwnerActorId.Should().Be(session.Scope.ActorId);
            bound.StagedSource.Transfer.TaskContentHash.Should().Be(session.IntentSha256);
        }
        ((long)await restart.GetDatabase().HashGetAsync(workerBudget.Key, "bytes")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task RetainedSource_ConcurrentCandidates_SelectOneOriginalGeneration()
    {
        using var disk = new StorageFixture();
        await using var firstConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var secondConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var first = new RedisImportUploadStore(firstConnection);
        var second = new RedisImportUploadStore(secondConnection);
        var (session, digest) = await StageAsync(first, disk.Storage, fullChunks: 0);
        (await first.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await first.GetAsync(session.Id, session.Scope))!;
        var publisher = new ImportUploadSourceStorage(first, disk.Storage, AllowStorageBudget(), Scratch(disk.Root));
        var a = await publisher.StageAsync(session, Path.Join(disk.Root, "scratch"), session.TotalBytes, static _ => Task.CompletedTask);
        var b = await publisher.StageAsync(session, Path.Join(disk.Root, "scratch"), session.TotalBytes, static _ => Task.CompletedTask);
        a.FileId.Should().NotBe(b.FileId);

        var accepted = await Task.WhenAll(first.TryBindSourceAsync(session, a), second.TryBindSourceAsync(session, b));

        accepted.Count(value => value).Should().Be(1);
        var winner = (await second.GetAsync(session.Id, session.Scope))!;
        winner.StagedSource.Should().Be(accepted[0] ? a : b);
        winner.Version.Should().Be(session.Version + 1);
        (await first.TryBindSourceAsync(winner, accepted[0] ? b : a)).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("revoked")]
    [InlineData("expired-session")]
    [InlineData("short-retention")]
    public async Task RetainedSource_WriteSucceedsButAuthorityOrLifetimeChanges_DoesNotReturnAcceptedSource(string change)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var revoked = false;
        CloudFile? written = null;
        var observed = Substitute.For<ICloudFileStorage>();
        observed.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.GetMetadataAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.DownloadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await disk.Storage.UploadAsync(call.ArgAt<FileUploadRequest>(0), call.ArgAt<CancellationToken>(1));
            result.Success.Should().BeTrue();
            written = result.File!;
            if (change == "revoked") revoked = true;
            if (change == "expired-session")
            {
                (await connection.GetDatabase().KeyExpireAsync(RedisImportUploadStore.Key(session.Id), TimeSpan.Zero))
                    .Should().BeTrue();
            }
            return change == "short-retention"
                ? result with { File = written with { ExpiresAt = session.ExpiresAt } }
                : result;
        });
        var scratchDirectory = Path.Join(disk.Root, "scratch");
        var publish = () => new ImportUploadSourceStorage(store, observed, AllowStorageBudget(), Scratch(disk.Root)).StageAsync(session,
            scratchDirectory, session.TotalBytes, _ =>
            {
                if (revoked) throw new UnauthorizedAccessException("Credential revoked during source write");
                return Task.CompletedTask;
            });

        if (change == "revoked") await FluentActions.Invoking(publish).Should().ThrowAsync<UnauthorizedAccessException>();
        else if (change == "expired-session") await FluentActions.Invoking(publish).Should().ThrowAsync<InvalidDataException>();
        else await FluentActions.Invoking(publish).Should().ThrowAsync<IOException>();

        written.Should().NotBeNull("the failure occurs after an actual storage write");
        written!.ExpiresAt.Should().NotBeNull("unaccepted candidates must have bounded retention");
        var current = await store.GetAsync(session.Id, session.Scope);
        if (change == "expired-session") current.Should().BeNull();
        else current.Should().Be(session, "no candidate may be bound after authorization or storage verification fails");
        Directory.EnumerateFiles(scratchDirectory).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task RetainedSource_LostBindReply_RecoversCommittedSourceWithoutReplacement()
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var source = await new ImportUploadSourceStorage(store, disk.Storage, AllowStorageBudget(), Scratch(disk.Root)).StageAsync(session,
            Path.Join(disk.Root, "scratch"), session.TotalBytes, static _ => Task.CompletedTask);
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0),
                    call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost source binding reply after actual commit");
            });
        var interrupted = Substitute.For<IConnectionMultiplexer>();
        interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        await FluentActions.Invoking(() => new RedisImportUploadStore(interrupted).TryBindSourceAsync(session, source))
            .Should().ThrowAsync<RedisConnectionException>();

        await using var restart = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var recovered = (await new RedisImportUploadStore(restart).GetAsync(session.Id, session.Scope))!;
        recovered.StagedSource.Should().Be(source);
        recovered.Version.Should().Be(session.Version + 1);
    }

    [IntegrationTheory]
    [InlineData("bytes")]
    [InlineData("digest")]
    [InlineData("expiry")]
    [InlineData("foreign")]
    public async Task RetainedSource_InvalidBindingCannotReplaceSealedIntent(string mutation)
    {
        using var disk = new StorageFixture();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisImportUploadStore(connection);
        var (session, digest) = await StageAsync(store, disk.Storage, fullChunks: 0);
        (await store.TrySealAsync(session, digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var source = new Honua.Core.Features.Import.Domain.ImportStagedSourceBinding(
            $"imports/resumable/{session.Id}/source-{Guid.NewGuid():N}", "generation", session.TotalBytes, digest,
            DateTimeOffset.UtcNow.AddDays(7));
        source = mutation switch
        {
            "bytes" => source with { Bytes = source.Bytes + 1 },
            "digest" => source with { Sha256 = new string('0', 64) },
            "expiry" => source with { ExpiresAt = session.ExpiresAt },
            _ => source with { FileId = "imports/resumable/foreign/source-file" }
        };
        await FluentActions.Invoking(() => store.TryBindSourceAsync(session, source)).Should().ThrowAsync<ArgumentException>();
        (await store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    private static async Task<(ImportUploadSession Session, string Digest)> StageAsync(
        RedisImportUploadStore store, LocalFileStorage storage, int fullChunks = 2)
    {
        var session = new ImportUploadSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Scope = new ImportUploadScope("owner", "tenant", "target", "workspace", "task", 1),
            IntentSha256 = new string('a', 64), FileName = "parcels.gpkg",
            TotalBytes = 65536L * fullChunks + 17, ChunkBytes = 65536,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        (await store.TryCreateAsync(session)).Should().BeTrue();
        var chunks = new ImportUploadChunkStorage(storage, AllowStorageBudget());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (session.AcceptedBytes < session.TotalBytes)
        {
            var bytes = new byte[(int)Math.Min(session.ChunkBytes, session.TotalBytes - session.AcceptedBytes)];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((session.AcceptedBytes + index + 1) % 251);
            }

            hash.AppendData(bytes);
            await using var input = new MemoryStream(bytes, writable: false);
            var chunk = await chunks.StageAsync(session, input, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            (await store.TryAppendAsync(session, chunk)).Should().BeTrue();
            session = (await store.GetAsync(session.Id, session.Scope))!;
        }

        return (session, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    // These cases isolate content/retention behavior; aggregate admission has real Redis/provider coverage.
    private static ImportScratchStorage Scratch(string root) => new(Path.Join(root, "scratch"), AllowStorageBudget());

    private static IImportStorageBudget AllowStorageBudget()
    {
        var budget = Substitute.For<IImportStorageBudget>();
        budget.TryReserveAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>()).Returns(true);
        return budget;
    }

    private sealed class StorageFixture : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-upload-file-" + Guid.NewGuid().ToString("N"));
        public LocalFileStorage Storage { get; }

        public StorageFixture() => Storage = OpenStorage();

        public LocalFileStorage OpenStorage() => new(
            Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
            NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
