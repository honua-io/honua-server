// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
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
        var chunks = new ImportUploadChunkStorage(storage);
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
