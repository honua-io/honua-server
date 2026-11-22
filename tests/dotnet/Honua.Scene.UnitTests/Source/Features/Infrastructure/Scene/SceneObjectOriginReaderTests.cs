// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.Scene.Assets;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneObjectOriginReaderTests : IDisposable
{
    private readonly string _workRoot;

    public SceneObjectOriginReaderTests()
    {
        _workRoot = Path.Join(Path.GetTempPath(), $"honua-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workRoot))
        {
            Directory.Delete(_workRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task OpenReadAsync_ObjectOriginWithoutLocalCache_StreamsSingleAssetWithoutHydratingSiblings()
    {
        var storage = new MetadataAwareInMemoryStorage();
        var prefix = SceneAssetHydration.BuildPrefix("replica-scene");
        var tileBytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"1.1\"}}");
        storage.Put($"{prefix}/{SceneAssetHydration.ManifestObjectName}", Encoding.UTF8.GetBytes("tileset.json\ntiles/0.b3dm\n"));
        storage.Put($"{prefix}/tileset.json", tileBytes);
        storage.Put($"{prefix}/tiles/0.b3dm", new byte[4096]);

        ISceneObjectOriginReader reader = new SceneObjectOriginReader(
            Options.Create(new SceneObjectOriginOptions()),
            NullLogger<SceneObjectOriginReader>.Instance,
            storage);

        var scene = new SceneDataset
        {
            Id = "replica-scene",
            Name = "Replica Scene",
            AssetRoot = Path.Join(_workRoot, "missing-local-root"),
            AssetStoragePrefix = prefix
        };

        await using var result = await reader.OpenReadAsync(scene, "tileset.json");
        result.Status.Should().Be(SceneObjectAssetReadStatus.Found);
        result.Metadata.Should().NotBeNull();
        result.Metadata!.FromObjectStorage.Should().BeTrue();

        using var ms = new MemoryStream();
        await result.Content!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(tileBytes);

        storage.RequestedKeys.Should().ContainSingle().Which.Should().Be($"{prefix}/tileset.json");
    }

    [UnitTest]
    public async Task OpenReadAsync_ObjectOriginRange_UsesRangeReaderWithoutDownloadingFullObject()
    {
        var storage = new MetadataAwareInMemoryStorage();
        var prefix = SceneAssetHydration.BuildPrefix("copc-origin");
        var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        var key = $"{prefix}/cloud.copc.laz";
        storage.Put(key, payload);
        var rangeReader = new FakeLocalRangeReader(storage);

        ISceneObjectOriginReader reader = new SceneObjectOriginReader(
            Options.Create(new SceneObjectOriginOptions()),
            NullLogger<SceneObjectOriginReader>.Instance,
            storage,
            [rangeReader]);

        var scene = new SceneDataset
        {
            Id = "copc-origin",
            Name = "COPC Origin",
            AssetRoot = Path.Join(_workRoot, "missing-copc-root"),
            AssetStoragePrefix = prefix,
            TilesetFileName = "cloud.copc.laz",
            ServingFormat = SceneServingFormat.Copc
        };

        await using var result = await reader.OpenReadAsync(scene, "cloud.copc.laz", new SceneByteRange(100, 64));
        result.Status.Should().Be(SceneObjectAssetReadStatus.Found);
        result.RangeStart.Should().Be(100);
        result.RangeEnd.Should().Be(163);

        using var ms = new MemoryStream();
        await result.Content!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload.AsSpan(100, 64).ToArray());

        rangeReader.RangeCallCount.Should().Be(1);
        storage.RequestedKeys.Should().BeEmpty("range reader should satisfy range without full DownloadAsync");
    }

    [UnitTest]
    public async Task OpenReadAsync_LocalRevisionRootRange_StreamsExactSlice()
    {
        var localRoot = Path.Join(_workRoot, "local-rev-1");
        Directory.CreateDirectory(localRoot);
        var payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        await File.WriteAllBytesAsync(Path.Join(localRoot, "sample.copc.laz"), payload);

        ISceneObjectOriginReader reader = new SceneObjectOriginReader(
            Options.Create(new SceneObjectOriginOptions()),
            NullLogger<SceneObjectOriginReader>.Instance);

        var scene = new SceneDataset
        {
            Id = "local-copc",
            Name = "Local COPC",
            AssetRoot = localRoot,
            TilesetFileName = "sample.copc.laz",
            ServingFormat = SceneServingFormat.Copc
        };

        await using var result = await reader.OpenReadAsync(scene, "sample.copc.laz", new SceneByteRange(32, 48));
        result.Status.Should().Be(SceneObjectAssetReadStatus.Found);
        result.Metadata!.FromObjectStorage.Should().BeFalse();

        using var ms = new MemoryStream();
        await result.Content!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload.AsSpan(32, 48).ToArray());
    }

    [UnitTest]
    public async Task OpenReadAsync_PathTraversalAndInternalControlFiles_Rejected()
    {
        var storage = new MetadataAwareInMemoryStorage();
        var prefix = SceneAssetHydration.BuildPrefix("guarded");
        storage.Put($"{prefix}/{SceneAssetHydration.ManifestObjectName}", Encoding.UTF8.GetBytes("tileset.json"));

        ISceneObjectOriginReader reader = new SceneObjectOriginReader(
            Options.Create(new SceneObjectOriginOptions()),
            NullLogger<SceneObjectOriginReader>.Instance,
            storage);

        var scene = new SceneDataset
        {
            Id = "guarded",
            Name = "Guarded",
            AssetRoot = _workRoot,
            AssetStoragePrefix = prefix
        };

        foreach (var badPath in new[] { "../etc/passwd", "/etc/passwd", "tiles/../../secret", "%2e%2e/secret", ".honua-manifest", ".honua-hydration" })
        {
            await using var result = await reader.OpenReadAsync(scene, badPath);
            result.Status.Should().Be(SceneObjectAssetReadStatus.InvalidPath, badPath);
        }

        storage.RequestedKeys.Should().BeEmpty();
    }

    [UnitTest]
    public async Task OpenReadAsync_ExceedsByteBudget_ReturnsByteBudgetExceeded()
    {
        var storage = new MetadataAwareInMemoryStorage();
        var prefix = SceneAssetHydration.BuildPrefix("budgeted");
        storage.Put($"{prefix}/huge.glb", new byte[2048]);

        ISceneObjectOriginReader reader = new SceneObjectOriginReader(
            Options.Create(new SceneObjectOriginOptions { MaxSingleAssetBytes = 1024, MaxRangeReadBytes = 256 }),
            NullLogger<SceneObjectOriginReader>.Instance,
            storage);

        var scene = new SceneDataset
        {
            Id = "budgeted",
            Name = "Budgeted",
            AssetRoot = Path.Join(_workRoot, "absent"),
            AssetStoragePrefix = prefix
        };

        await using var full = await reader.OpenReadAsync(scene, "huge.glb");
        full.Status.Should().Be(SceneObjectAssetReadStatus.ByteBudgetExceeded);

        await using var oversizedRange = await reader.OpenReadAsync(scene, "huge.glb", new SceneByteRange(0, 512));
        oversizedRange.Status.Should().Be(SceneObjectAssetReadStatus.ByteBudgetExceeded);

        await using var outOfBounds = await reader.OpenReadAsync(scene, "huge.glb", new SceneByteRange(5000, 10));
        outOfBounds.Status.Should().Be(SceneObjectAssetReadStatus.RangeUnsatisfiable);
    }

    private sealed class MetadataAwareInMemoryStorage : ICloudFileStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        public List<string> RequestedKeys { get; } = [];
        public CloudStorageProvider Provider => CloudStorageProvider.Local;

        public void Put(string key, byte[] bytes) => _objects[key] = bytes;

        public bool TryGet(string key, out byte[] bytes) => _objects.TryGetValue(key, out bytes!);

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
        {
            RequestedKeys.Add(fileId);
            return Task.FromResult<Stream?>(
                _objects.TryGetValue(fileId, out var bytes) ? new Honua.TestKit.CallerOwnedMemoryStream(bytes, writable: false) : null);
        }

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_objects.TryGetValue(fileId, out var bytes)
                ? new CloudFile
                {
                    FileId = fileId,
                    FileName = Path.GetFileName(fileId),
                    StoragePath = fileId,
                    ContentType = "application/octet-stream",
                    SizeBytes = bytes.Length,
                    ETag = $"\"etag-{bytes.Length}\"",
                    UploadedAt = DateTimeOffset.UtcNow,
                    Provider = CloudStorageProvider.Local
                }
                : null);

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_objects.ContainsKey(fileId));

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, bool includeMetadata = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(string fileName, string contentType, TimeSpan? expiresIn = null, string? folder = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeLocalRangeReader(MetadataAwareInMemoryStorage storage) : ICloudRangeReader
    {
        public int RangeCallCount { get; private set; }
        public CloudStorageProvider Provider => CloudStorageProvider.Local;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            RangeCallCount++;
            storage.TryGet(key, out var bytes);
            return Task.FromResult(bytes.AsSpan((int)offset, length).ToArray());
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            RangeCallCount++;
            storage.TryGet(key, out var bytes);
            return Task.FromResult<Stream>(new Honua.TestKit.CallerOwnedMemoryStream(bytes.AsSpan((int)offset, length).ToArray(), writable: false));
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            storage.TryGet(key, out var bytes);
            return Task.FromResult<long>(bytes.Length);
        }
    }
}
