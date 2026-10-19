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
public sealed class ImportUploadStorageAdmissionTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task Stage_MissingBudget_DoesNotConsumeBodyOrWriteStorage()
    {
        using var disk = new Disk();
        await using var input = new MemoryStream(Bytes, writable: false);

        await FluentActions.Invoking(() => new ImportUploadChunkStorage(disk.Storage)
            .StageAsync(Session(), input, Digest)).Should().ThrowAsync<InvalidOperationException>();

        input.Position.Should().Be(0);
        (await disk.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Stage_WriteCommitsButReplyIsLost_RemainsChargedAndRetryCannotExceedBudget()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var disk = new Disk();
        var budget = Budget(connection, disk.Root, Bytes.Length);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var observed = Substitute.For<ICloudFileStorage>();
        observed.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // The reservation must already exist when the provider receives its first write.
                ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes.Length);
                (await disk.Storage.UploadAsync(call.ArgAt<FileUploadRequest>(0), call.ArgAt<CancellationToken>(1)))
                    .Success.Should().BeTrue();
                throw new IOException("Reply lost after storing actual bytes");
            });
        var session = Session();
        await using var first = new MemoryStream(Bytes, writable: false);
        await FluentActions.Invoking(() => new ImportUploadChunkStorage(observed, budget)
            .StageAsync(session, first, Digest)).Should().ThrowAsync<IOException>();

        await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var retry = new MemoryStream(Bytes, writable: false);
        await FluentActions.Invoking(() => new ImportUploadChunkStorage(disk.Storage,
            Budget(restarted, disk.Root, Bytes.Length)).StageAsync(session, retry, Digest))
            .Should().ThrowAsync<InvalidOperationException>();

        (await disk.Storage.ListFilesAsync()).Should().ContainSingle();
        ((long)await restarted.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes.Length);
        ((long)await restarted.GetDatabase().HashGetAsync(budget.Key, "count")).Should().Be(1);
        (await restarted.GetDatabase().KeyTimeToLiveAsync(budget.Key)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task Stage_ReservationCommitsButReplyIsLost_DoesNotWriteAndRetainsCharge()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var disk = new Disk();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0),
                    call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost reservation response");
            });
        var interrupted = Substitute.For<IConnectionMultiplexer>();
        interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(Budget(connection, disk.Root, Bytes.Length));
        var budget = Budget(interrupted, disk.Root, Bytes.Length);
        await using var input = new MemoryStream(Bytes, writable: false);

        await FluentActions.Invoking(() => new ImportUploadChunkStorage(disk.Storage, budget)
            .StageAsync(Session(), input, Digest)).Should().ThrowAsync<RedisConnectionException>();

        (await disk.Storage.ListFilesAsync()).Should().BeEmpty();
        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes.Length);
    }

    [IntegrationTheory]
    [InlineData("confirmed")]
    [InlineData("replacement")]
    [InlineData("lost-reply")]
    public async Task Cleanup_OnlyConfirmedExactGenerationDeletionReleasesStorage(string outcome)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var disk = new Disk();
        var budget = Budget(connection, disk.Root, Bytes.Length);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var session = Session();
        var chunks = new ImportUploadChunkStorage(disk.Storage, budget);
        await using var input = new MemoryStream(Bytes, writable: false);
        var candidate = await chunks.StageAsync(session, input, Digest);
        if (outcome == "replacement")
        {
            await using var replacement = new MemoryStream(Bytes, writable: false);
            (await disk.Storage.UploadAsync(new FileUploadRequest
            {
                Content = replacement, FileName = "replacement.part", ContentType = "application/octet-stream",
                ObjectKeyOverride = candidate.FileId
            })).Success.Should().BeTrue();
        }

        if (outcome == "lost-reply")
        {
            var interrupted = Substitute.For<ICloudFileStorage>();
            interrupted.DeleteIfMatchAsync(candidate.FileId, candidate.ETag, Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    (await disk.Storage.DeleteIfMatchAsync(candidate.FileId, candidate.ETag, call.ArgAt<CancellationToken>(2)))
                        .Should().BeTrue();
                    throw new IOException("Lost deletion response");
                });
            await FluentActions.Invoking(() => new ImportUploadChunkStorage(interrupted, budget)
                .DeleteUnacceptedAsync(session, candidate)).Should().ThrowAsync<IOException>();
        }
        else
        {
            (await chunks.DeleteUnacceptedAsync(session, candidate)).Should().Be(outcome == "confirmed");
        }

        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes"))
            .Should().Be(outcome == "confirmed" ? 0 : Bytes.Length);
        (await disk.Storage.ListFilesAsync()).Count.Should().Be(outcome == "replacement" ? 1 : 0);
        // A repeated cleanup cannot release a charge merely because the object is now absent.
        (await chunks.DeleteUnacceptedAsync(session, candidate)).Should().BeFalse();
        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes"))
            .Should().Be(outcome == "confirmed" ? 0 : Bytes.Length);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retain_AccountsForBothChunksAndRetainedSourceBeforeWriting(bool sourceFits)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var disk = new Disk();
        var budget = Budget(connection, disk.Root, Bytes.Length * (sourceFits ? 2 : 1));
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var store = new RedisImportUploadStore(connection);
        var session = Session();
        (await store.TryCreateAsync(session)).Should().BeTrue();
        await using var input = new MemoryStream(Bytes, writable: false);
        var chunk = await new ImportUploadChunkStorage(disk.Storage, budget).StageAsync(session, input, Digest);
        (await store.TryAppendAsync(session, chunk)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        (await store.TrySealAsync(session, Digest)).Should().BeTrue();
        session = (await store.GetAsync(session.Id, session.Scope))!;
        var observed = Substitute.For<ICloudFileStorage>();
        var scratchBudget = Budget(connection, disk.Root + ":scratch", Bytes.Length);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(scratchBudget);
        observed.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.GetMetadataAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => disk.Storage.DownloadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(2 * Bytes.Length);
                ((long)await connection.GetDatabase().HashGetAsync(scratchBudget.Key, "bytes")).Should().Be(Bytes.Length);
                return await disk.Storage.UploadAsync(call.ArgAt<FileUploadRequest>(0), call.ArgAt<CancellationToken>(1));
            });
        var scratch = Path.Join(disk.Root, "scratch");
        var publish = () => new ImportUploadSourceStorage(store, observed, budget,
            new ImportScratchStorage(scratch, scratchBudget))
            .StageAsync(session, scratch, session.TotalBytes, _ => Task.CompletedTask);

        if (sourceFits)
        {
            var source = await publish();
            source.Bytes.Should().Be(Bytes.Length);
            source.Sha256.Should().Be(Digest);
        }
        else
        {
            await FluentActions.Invoking(publish).Should().ThrowAsync<InvalidOperationException>();
            await observed.DidNotReceive().UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
        }

        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes"))
            .Should().Be(Bytes.Length * (sourceFits ? 2 : 1));
        (await disk.Storage.ListFilesAsync()).Count.Should().Be(sourceFits ? 2 : 1);
        Directory.GetFiles(scratch).Should().BeEmpty();
        ((long)await connection.GetDatabase().HashGetAsync(scratchBudget.Key, "bytes")).Should().Be(0);
    }

    private static readonly byte[] Bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Bytes));

    private static RedisImportStorageBudget Budget(IConnectionMultiplexer connection, string domain, long bytes) =>
        ImportStorageBudgetTestFactory.Bind(connection, domain, new ImportStorageBudgetLimits(bytes, bytes, bytes, 100));

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Scope = new("owner", "tenant", "target", "workspace", "task", 1),
        IntentSha256 = new string('a', 64), FileName = "parcels.gpkg", TotalBytes = Bytes.Length,
        ChunkBytes = Bytes.Length, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private sealed class Disk : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-upload-budget-" + Guid.NewGuid().ToString("N"));
        public LocalFileStorage Storage { get; }

        public Disk() => Storage = new LocalFileStorage(
            Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
            NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
