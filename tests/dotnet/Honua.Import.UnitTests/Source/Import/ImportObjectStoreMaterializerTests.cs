// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportObjectStoreMaterializerTests
{
    private const long SixGiB = 6L * 1024 * 1024 * 1024;

    [UnitTest]
    public async Task RandomAccessCopy_RecordsTtlSizeAndCleanup()
    {
        var storage = new RecordingStorage();
        await using var body = new ForwardOnlyStream([1, 2, 3, 4]);
        var request = Source("roads.zip");

        var result = await ImportObjectStoreMaterializer.MaterializeAsync(
            request,
            new BytesReader(body),
            storage,
            AllowBucket(),
            CancellationToken.None,
            contentLength: SixGiB);

        result.DeleteCloudFile.Should().BeTrue();
        result.DisposeStream.Should().BeFalse();
        result.Request.CloudFileId.Should().Be("staged-copy");
        result.Request.ObjectStoreUri.Should().BeNull();
        storage.Upload.Should().NotBeNull();
        storage.Upload!.SizeBytes.Should().Be(SixGiB);
        storage.Upload.TimeToLive.Should().Be(ImportObjectStoreMaterializer.StagedCopyLifetime);
        storage.Upload.Folder.Should().Be("imports");
        storage.Upload.FileName.Should().Be("roads.zip");
    }

    [UnitTest]
    public async Task RandomAccessCopy_OmitsSizeWhenTheLengthIsUnknown()
    {
        var storage = new RecordingStorage();
        await using var body = new ForwardOnlyStream([1]);

        await ImportObjectStoreMaterializer.MaterializeAsync(
            Source("roads.gpkg"),
            new BytesReader(body),
            storage,
            AllowBucket(),
            CancellationToken.None);

        storage.Upload!.SizeBytes.Should().BeNull();
        storage.Upload.TimeToLive.Should().Be(ImportObjectStoreMaterializer.StagedCopyLifetime);
    }

    [UnitTest]
    public async Task StreamableObject_IsNotCopiedToCloudStorage()
    {
        var storage = new RecordingStorage();
        await using var body = new MemoryStream([1, 2, 3]);

        var result = await ImportObjectStoreMaterializer.MaterializeAsync(
            Source("parcels.geojson"),
            new BytesReader(body),
            storage,
            AllowBucket(),
            CancellationToken.None,
            contentLength: SixGiB);

        result.DeleteCloudFile.Should().BeFalse();
        result.DisposeStream.Should().BeTrue();
        result.Request.FileStream.Should().NotBeNull();
        storage.Upload.Should().BeNull();
        await result.Request.FileStream!.DisposeAsync();
    }

    private static ImportRequest Source(string fileName)
        => new()
        {
            ObjectStoreUri = "s3://bucket/layers/" + fileName,
            ObjectStoreETag = "\"pin\"",
            FileName = fileName,
            TableName = "layers",
        };

    private static ImportObjectStoreOptions AllowBucket()
        => new() { AllowedBuckets = ["bucket"] };

    private sealed class BytesReader(Stream body) : IImportObjectStoreReader
    {
        public Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken)
            => Task.FromResult(body);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, data.Length - _offset);
            data.AsSpan(_offset, available).CopyTo(buffer.AsSpan(offset, available));
            _offset += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingStorage : ICloudFileStorage
    {
        public FileUploadRequest? Upload { get; private set; }

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            Upload = request;
            return Task.FromResult(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = "staged-copy",
                FileName = request.FileName,
                StoragePath = "imports/staged-copy",
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes ?? 0,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3,
            }));
        }

        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
            string? folder = null,
            int maxResults = 1000,
            bool includeMetadata = true,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
            string fileName,
            string contentType,
            TimeSpan? expiresIn = null,
            string? folder = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
