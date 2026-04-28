// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

public sealed class PMTilesProxyServiceTests
{
    [Theory]
    [InlineData("bytes=0-9", 0L, 9L)]
    [InlineData("bytes=10-19", 10L, 19L)]
    [InlineData("bytes=0-", 0L, 99L)]
    [InlineData("bytes=50-", 50L, 99L)]
    [InlineData("bytes=-10", 90L, 99L)]
    public void TryParseSingleByteRange_ValidRanges_ReturnsTrue(string header, long expectedStart, long expectedEnd)
    {
        var ok = PMTilesProxyService.TryParseSingleByteRange(header, totalSize: 100, out var start, out var end);
        ok.Should().BeTrue();
        start.Should().Be(expectedStart);
        end.Should().Be(expectedEnd);
    }

    [Theory]
    [InlineData("bytes=100-200")]
    [InlineData("bytes=150-")]
    [InlineData("bytes=-0")]
    [InlineData("garbage")]
    public void TryParseSingleByteRange_OutOfRange_ReturnsFalse(string header)
    {
        var ok = PMTilesProxyService.TryParseSingleByteRange(header, totalSize: 100, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_MissingArtifact_ReturnsNotFound()
    {
        var stub = new TestCloudStorage();
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var resolution = await sut.ResolveAsync("missing", CancellationToken.None);

        resolution.Exists.Should().BeFalse();
        resolution.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_NonPMTilesContentType_ReturnsNotFound()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile(
            [0, 1, 2, 3],
            contentType: "application/octet-stream",
            metadata: ImmutableDictionary<string, string>.Empty.Add("operation", "publish"));
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var resolution = await sut.ResolveAsync(fileId, CancellationToken.None);

        resolution.Exists.Should().BeFalse();
        resolution.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_MissingPublishOperationTag_ReturnsNotFound()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile(
            [0, 1, 2, 3],
            contentType: "application/vnd.pmtiles",
            metadata: ImmutableDictionary<string, string>.Empty.Add("operation", "archive"));
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var resolution = await sut.ResolveAsync(fileId, CancellationToken.None);

        resolution.Exists.Should().BeFalse();
        resolution.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_KeyOutsidePublishPrefix_ReturnsNotFound()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile(
            [0, 1, 2, 3],
            contentType: "application/vnd.pmtiles",
            metadata: ImmutableDictionary<string, string>.Empty.Add("operation", "publish"),
            storagePath: "secrets/leaked-credentials.bin");
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options { BucketName = "honua-bucket", Region = "us-east-1" },
            PMTilesPublish = new PMTilesPublishOptions { KeyPrefix = "pmtiles" }
        });
        var sut = new PMTilesProxyService(stub, [], options);

        var resolution = await sut.ResolveAsync(fileId, CancellationToken.None);

        resolution.Exists.Should().BeFalse();
        resolution.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ProviderPrefixedPublishKey_IsAccepted()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile(
            [0, 1, 2, 3],
            contentType: "application/vnd.pmtiles",
            metadata: ImmutableDictionary<string, string>.Empty.Add("operation", "publish"),
            storagePath: "tenants/acme/pmtiles/world/42/WebMercatorQuad.pmtiles");
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "honua-bucket",
                Region = "us-east-1",
                KeyPrefix = "tenants/acme"
            },
            PMTilesPublish = new PMTilesPublishOptions { KeyPrefix = "pmtiles" }
        });
        var sut = new PMTilesProxyService(stub, [], options);

        var resolution = await sut.ResolveAsync(fileId, CancellationToken.None);

        resolution.Exists.Should().BeTrue();
        resolution.Metadata.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRangeAsync_NoRangeHeader_ReturnsFull()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile([0, 1, 2, 3, 4, 5, 6, 7]);
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var metadata = (await sut.ResolveAsync(fileId, CancellationToken.None)).Metadata!;
        var result = await sut.ReadRangeAsync(metadata, rangeHeader: null, CancellationToken.None);

        result.Outcome.Should().Be(PMTilesRangeOutcome.Full);
        result.TotalSize.Should().Be(8);
    }

    [Fact]
    public async Task ReadRangeAsync_PartialRange_ReturnsRequestedBytes_ViaDownloadFallback()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile([10, 20, 30, 40, 50, 60, 70, 80]);
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var metadata = (await sut.ResolveAsync(fileId, CancellationToken.None)).Metadata!;
        var result = await sut.ReadRangeAsync(metadata, rangeHeader: "bytes=2-5", CancellationToken.None);

        result.Outcome.Should().Be(PMTilesRangeOutcome.Partial);
        result.Start.Should().Be(2);
        result.End.Should().Be(5);
        result.TotalSize.Should().Be(8);
        result.Payload.Should().Equal(30, 40, 50, 60);
    }

    [Fact]
    public async Task ReadRangeAsync_OutOfRange_ReturnsUnsatisfiable()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile([1, 2, 3]);
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var metadata = (await sut.ResolveAsync(fileId, CancellationToken.None)).Metadata!;
        var result = await sut.ReadRangeAsync(metadata, rangeHeader: "bytes=20-30", CancellationToken.None);

        result.Outcome.Should().Be(PMTilesRangeOutcome.Unsatisfiable);
        result.TotalSize.Should().Be(3);
    }

    [Fact]
    public async Task ReadRangeAsync_DownloadFallback_ReturnsNotFound_WhenUnderlyingObjectMissing()
    {
        // Simulate metadata still present (e.g., cached) but the underlying object
        // having been deleted out-of-band. The full-download path returns 404 for
        // this; the range-read fallback must do the same instead of throwing 500.
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile([1, 2, 3, 4, 5, 6, 7, 8]);
        stub.SimulateOutOfBandDelete(fileId);
        var sut = new PMTilesProxyService(stub, [], Options.Create(new CloudStorageOptions()));

        var metadata = (await sut.ResolveAsync(fileId, CancellationToken.None)).Metadata;
        metadata.Should().NotBeNull("metadata is still cached even though the bytes are gone");

        var result = await sut.ReadRangeAsync(metadata!, rangeHeader: "bytes=0-3", CancellationToken.None);

        result.Outcome.Should().Be(PMTilesRangeOutcome.NotFound);
        result.TotalSize.Should().Be(8);
    }

    [Fact]
    public async Task ReadRangeAsync_RangeReaderRegistered_DispatchesByProvider()
    {
        var stub = new TestCloudStorage();
        var fileId = stub.AddFile(new byte[16], provider: CloudStorageProvider.AwsS3);
        var stubReader = new RecordingRangeReader(CloudStorageProvider.AwsS3);
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options { BucketName = "honua-bucket", Region = "us-east-1" }
        });
        var sut = new PMTilesProxyService(stub, [stubReader], options);

        var metadata = (await sut.ResolveAsync(fileId, CancellationToken.None)).Metadata!;
        var result = await sut.ReadRangeAsync(metadata, "bytes=0-3", CancellationToken.None);

        result.Outcome.Should().Be(PMTilesRangeOutcome.Partial);
        stubReader.LastBucket.Should().Be("honua-bucket");
        stubReader.LastKey.Should().Be(metadata.StoragePath);
        stubReader.LastOffset.Should().Be(0);
        stubReader.LastLength.Should().Be(4);
    }

    private sealed class TestCloudStorage : ICloudFileStorage
    {
        private static readonly ImmutableDictionary<string, string> DefaultPublishMetadata =
            ImmutableDictionary<string, string>.Empty.Add("operation", "publish");

        private readonly Dictionary<string, (CloudFile File, byte[] Bytes)> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingBytes = new(StringComparer.Ordinal);

        public CloudStorageProvider Provider => CloudStorageProvider.Local;

        public string AddFile(
            byte[] bytes,
            CloudStorageProvider provider = CloudStorageProvider.Local,
            string contentType = "application/vnd.pmtiles",
            ImmutableDictionary<string, string>? metadata = null,
            string? storagePath = null)
        {
            var fileId = Guid.NewGuid().ToString("N");
            _files[fileId] = (new CloudFile
            {
                FileId = fileId,
                FileName = $"{fileId}.bin",
                StoragePath = storagePath ?? $"pmtiles/{fileId}",
                ContentType = contentType,
                SizeBytes = bytes.Length,
                UploadedAt = DateTimeOffset.UtcNow,
                Metadata = metadata ?? DefaultPublishMetadata,
                Provider = provider
            }, bytes);
            return fileId;
        }

        /// <summary>
        /// Marks the bytes for the given file as missing while keeping the
        /// metadata entry. Mirrors an out-of-band delete (lifecycle expiry,
        /// console deletion) where <c>GetMetadataAsync</c> still returns the
        /// stale record but <c>DownloadAsync</c> returns null.
        /// </summary>
        public void SimulateOutOfBandDelete(string fileId) => _missingBytes.Add(fileId);

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default) => Task.FromResult<UploadProgress?>(null);
        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UploadProgress>>([]);

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(!_missingBytes.Contains(fileId) && _files.TryGetValue(fileId, out var entry)
                ? new MemoryStream(entry.Bytes, writable: false)
                : null);

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(!_missingBytes.Contains(fileId) && _files.TryGetValue(fileId, out var entry) ? entry.Bytes : null);

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default) => Task.FromResult(_files.Remove(fileId));
        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default) => Task.FromResult(_files.TryGetValue(fileId, out var entry) ? entry.File : null);
        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default) => Task.FromResult(_files.ContainsKey(fileId));

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudFile>>(_files.Values.Select(e => e.File).ToArray());

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(string fileName, string contentType, TimeSpan? expiresIn = null, string? folder = null, CancellationToken cancellationToken = default) => Task.FromResult<(string Url, string FileId)?>(null);
        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingRangeReader : ICloudRangeReader
    {
        public RecordingRangeReader(CloudStorageProvider provider)
        {
            Provider = provider;
        }

        public CloudStorageProvider Provider { get; }
        public string? LastBucket { get; private set; }
        public string? LastKey { get; private set; }
        public long LastOffset { get; private set; }
        public int LastLength { get; private set; }

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            LastBucket = bucket;
            LastKey = key;
            LastOffset = offset;
            LastLength = length;
            return Task.FromResult(new byte[length]);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[length]));

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }
}
