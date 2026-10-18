// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Import.FileImport.Resumable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed class ImportUploadChunkStorageTests
{
    [UnitTest]
    public async Task StageAndRead_LocalStorage_RoundTripsExactBytesWithIndependentGenerations()
    {
        using var fixture = new StorageFixture();
        var session = Session();
        var bytes = Content(session.ChunkBytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var firstInput = new MemoryStream(bytes, writable: false);
        await using var secondInput = new MemoryStream(bytes, writable: false);

        var first = await fixture.Chunks.StageAsync(session, firstInput, sha);
        var second = await fixture.Chunks.StageAsync(session, secondInput, sha);

        first.Index.Should().Be(0);
        first.Offset.Should().Be(0);
        first.Bytes.Should().Be(bytes.Length);
        first.Sha256.Should().Be(sha);
        first.FileId.Should().StartWith($"imports/resumable/{session.Id}/");
        second.FileId.Should().NotBe(first.FileId);
        second.ETag.Should().NotBe(first.ETag);
        await using var verified = await fixture.Chunks.OpenVerifiedAsync(first);
        verified.CanWrite.Should().BeFalse();
        using var downloaded = new MemoryStream();
        await verified.CopyToAsync(downloaded);
        downloaded.ToArray().Should().Equal(bytes);
        var metadata = (await fixture.Storage.GetMetadataAsync(first.FileId))!;
        metadata.Metadata["sha256"].Should().Be(sha);
        metadata.ExpiresAt.Should().BeAfter(session.ExpiresAt);
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Stage_TruncatedExcessOrChangedBody_DoesNotCreateStorageObject(int lengthDelta)
    {
        using var fixture = new StorageFixture();
        var session = Session();
        var original = Content(session.ChunkBytes);
        var body = Content(session.ChunkBytes + lengthDelta);
        if (lengthDelta == 0)
        {
            body[body.Length / 2] ^= 1;
        }

        await using var source = new MemoryStream(body, writable: false);
        await FluentActions.Invoking(() => fixture.Chunks.StageAsync(session, source,
                Convert.ToHexStringLower(SHA256.HashData(original))))
            .Should().ThrowAsync<InvalidDataException>();
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Stage_EndlessNonSeekableBody_StopsAtExactlyOneBytePastChunkBoundary()
    {
        using var fixture = new StorageFixture();
        var session = Session();
        await using var endless = new EndlessReadStream();

        await FluentActions.Invoking(() => fixture.Chunks.StageAsync(session, endless, new string('a', 64)))
            .Should().ThrowAsync<InvalidDataException>();

        endless.BytesRead.Should().Be(session.ChunkBytes + 1L);
        endless.LargestRead.Should().BeLessThanOrEqualTo(64 * 1024);
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Stage_MultiGigabyteSession_TransfersOnlyTheDeclaredFirstChunk()
    {
        using var fixture = new StorageFixture();
        var session = Session() with { TotalBytes = 9L * 1024 * 1024 * 1024, ChunkBytes = RedisImportUploadStore.MaximumChunkBytes };
        var bytes = Content(session.ChunkBytes);
        await using var source = new MemoryStream(bytes, writable: false);

        var chunk = await fixture.Chunks.StageAsync(session, source, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        chunk.Bytes.Should().Be(4 * 1024 * 1024);
        (await fixture.Storage.GetMetadataAsync(chunk.FileId))!.SizeBytes.Should().Be(bytes.Length);
        // A real 4 MiB chunk under a 9 GiB declaration, not a complete 9 GiB upload claim.
        source.Position.Should().Be(bytes.Length);
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Read_StoredBytesChangedWithoutMetadataUpdate_RejectsBeforeExposingContent(int lengthDelta)
    {
        using var fixture = new StorageFixture();
        var session = Session();
        var original = Content(session.ChunkBytes);
        await using var input = new MemoryStream(original, writable: false);
        var chunk = await fixture.Chunks.StageAsync(session, input, Convert.ToHexStringLower(SHA256.HashData(original)));
        var metadata = (await fixture.Storage.GetMetadataAsync(chunk.FileId))!;
        var corrupt = Content(original.Length + lengthDelta);
        if (lengthDelta == 0)
        {
            corrupt[0] ^= 1;
        }

        await File.WriteAllBytesAsync(Path.Join(fixture.Root, metadata.StoragePath), corrupt);

        await FluentActions.Invoking(() => fixture.Chunks.OpenVerifiedAsync(chunk))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [UnitTest]
    public async Task ReadAndCleanup_StaleGeneration_CannotReadOrDeleteReplacement()
    {
        using var fixture = new StorageFixture();
        var bytes = Content(65536);
        await using var input = new MemoryStream(bytes, writable: false);
        var chunk = await fixture.Chunks.StageAsync(Session(), input, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await using var replacement = new MemoryStream(bytes, writable: false);
        var changed = await fixture.Storage.UploadAsync(new FileUploadRequest
        {
            Content = replacement,
            FileName = "replacement.part",
            ContentType = "application/octet-stream",
            ObjectKeyOverride = chunk.FileId
        });
        changed.Success.Should().BeTrue();

        await FluentActions.Invoking(() => fixture.Chunks.OpenVerifiedAsync(chunk)).Should().ThrowAsync<InvalidDataException>();
        (await fixture.Chunks.DeleteUnacceptedAsync(chunk)).Should().BeFalse();
        (await fixture.Storage.GetMetadataAsync(chunk.FileId))!.ETag.Should().Be(changed.File!.ETag);
    }

    [UnitTest]
    public async Task Read_GenerationChangesDuringDownload_RejectsEvenMatchingBytes()
    {
        var bytes = Content(65536);
        var chunk = new ImportUploadChunk(0, 0, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "stored-object", "original");
        var before = new CloudFile
        {
            FileId = chunk.FileId, FileName = "chunk.part", StoragePath = chunk.FileId,
            ContentType = "application/octet-stream", SizeBytes = bytes.Length,
            UploadedAt = DateTimeOffset.UtcNow, ETag = chunk.ETag, Provider = CloudStorageProvider.Local
        };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(chunk.FileId, Arg.Any<CancellationToken>()).Returns(before, before with { ETag = "replacement" });
        storage.DownloadAsync(chunk.FileId, Arg.Any<CancellationToken>()).Returns(new MemoryStream(bytes, writable: false));

        await FluentActions.Invoking(() => new ImportUploadChunkStorage(storage).OpenVerifiedAsync(chunk))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [UnitTest]
    public async Task Stage_FinalPartialChunk_PreservesItsActualRange()
    {
        using var fixture = new StorageFixture();
        var session = Session() with { AcceptedBytes = 65536, ChunkCount = 1, Version = 2 };
        var bytes = Content(17);
        await using var input = new MemoryStream(bytes, writable: false);

        var chunk = await fixture.Chunks.StageAsync(session, input, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        chunk.Index.Should().Be(1);
        chunk.Offset.Should().Be(65536);
        chunk.Bytes.Should().Be(17);
        await using var verified = await fixture.Chunks.OpenVerifiedAsync(chunk);
        using var output = new MemoryStream();
        await verified.CopyToAsync(output);
        output.ToArray().Should().Equal(bytes);
    }

    [UnitTheory]
    [InlineData((int)ImportUploadState.Cancelled)]
    [InlineData((int)ImportUploadState.Sealed)]
    public async Task Stage_ClosedSession_DoesNotReadBodyOrWriteStorage(int stateValue)
    {
        using var fixture = new StorageFixture();
        var state = (ImportUploadState)stateValue;
        var session = Session() with { State = state };
        if (state == ImportUploadState.Sealed)
        {
            session = session with { AcceptedBytes = session.TotalBytes, ChunkCount = 2, ContentSha256 = new string('a', 64) };
        }

        await using var source = new EndlessReadStream();
        await FluentActions.Invoking(() => fixture.Chunks.StageAsync(session, source, new string('a', 64)))
            .Should().ThrowAsync<InvalidOperationException>();
        source.BytesRead.Should().Be(0);
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Stage_CancelledToken_DoesNotReadBodyOrWriteStorage()
    {
        using var fixture = new StorageFixture();
        await using var source = new EndlessReadStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => fixture.Chunks.StageAsync(Session(), source, new string('a', 64), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        source.BytesRead.Should().Be(0);
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Scope = new ImportUploadScope("owner", "tenant", "target", "workspace", "task", 1),
        IntentSha256 = new string('a', 64), FileName = "parcels.gpkg", TotalBytes = 65536 + 17,
        ChunkBytes = 65536, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static byte[] Content(int length) => Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    private sealed class StorageFixture : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-upload-chunks-" + Guid.NewGuid().ToString("N"));
        public LocalFileStorage Storage { get; }
        public ImportUploadChunkStorage Chunks { get; }

        public StorageFixture()
        {
            Storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
                NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
            Chunks = new ImportUploadChunkStorage(Storage);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class EndlessReadStream : Stream
    {
        public long BytesRead { get; private set; }
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            BytesRead += buffer.Length;
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return ValueTask.FromResult(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
