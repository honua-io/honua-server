// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class TemporaryFileServiceTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"honua-temp-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreTemporaryFileAsync_ExceedingTotalStorageLimit_ThrowsLimitExceeded()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1100,
            MaxFileCount = 100,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[20],
            "image/png");

        Func<Task> act = async () => await service.StoreTemporaryFileAsync(
            new byte[20],
            "image/png");

        await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_ExceedingFileCountLimit_ThrowsLimitExceeded()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        Func<Task> act = async () => await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_RemovesExpiredFilesBeforeQuotaCheck()
    {
        var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png",
            expiration: TimeSpan.FromMilliseconds(10));

        await Task.Delay(50);

        Func<Task<string>> act = async () => await service.StoreTemporaryFileAsync(
            new byte[8],
            "image/png");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_MultipleInstancesShareQuotaLimits()
    {
        var options = new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 1,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        };

        using var firstService = CreateService(options);
        using var secondService = CreateService(options);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> StoreAsync(FileSystemTemporaryFileService service)
        {
            await start.Task;

            try
            {
                await service.StoreTemporaryFileAsync(new byte[128 * 1024], "image/png");
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var firstStore = StoreAsync(firstService);
        var secondStore = StoreAsync(secondService);

        start.SetResult();
        var results = await Task.WhenAll(firstStore, secondStore);

        results.Count(result => result is null).Should().Be(1);
        results.Count(result => result is TemporaryStorageLimitExceededException).Should().Be(1);
    }

    [Fact]
    public async Task CleanupExpiredFilesAsync_RemovesStaleOrphanedDataFilesWithoutMetadata()
    {
        using var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 10,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        var orphanPath = Path.Combine(_storageDirectory, $"{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(_storageDirectory);
        await File.WriteAllBytesAsync(orphanPath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        await service.CleanupExpiredFilesAsync();

        File.Exists(orphanPath).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupExpiredFilesAsync_DoesNotRemoveRecentOrphanedDataFilesWithoutMetadata()
    {
        using var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 10,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        var orphanPath = Path.Combine(_storageDirectory, $"{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(_storageDirectory);
        await File.WriteAllBytesAsync(orphanPath, [1, 2, 3, 4]);

        await service.CleanupExpiredFilesAsync();

        File.Exists(orphanPath).Should().BeTrue();
    }

    [Fact]
    public async Task GetTemporaryFileAsync_WithMismatchedPrincipal_ReturnsNull()
    {
        using var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 10,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        var owner = CreatePrincipal("owner");
        var other = CreatePrincipal("other");
        var url = await service.StoreTemporaryFileAsync(
            new byte[] { 1, 2, 3 },
            "image/png",
            principal: owner);

        var fileId = Path.GetFileName(url);
        var result = await service.GetTemporaryFileAsync(fileId, principal: other);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetTemporaryFileAsync_WithMatchingPrincipal_ReturnsFile()
    {
        using var service = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            MaxFileSizeBytes = 1024 * 1024,
            MaxTotalStorageBytes = 1024 * 1024,
            MaxFileCount = 10,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });

        var owner = CreatePrincipal("owner");
        var url = await service.StoreTemporaryFileAsync(
            new byte[] { 1, 2, 3 },
            "image/png",
            principal: owner);

        var fileId = Path.GetFileName(url);
        var result = await service.GetTemporaryFileAsync(fileId, principal: owner);

        result.Should().NotBeNull();
        result!.Value.contentType.Should().Be("image/png");
        result.Value.data.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithoutExplicitPrincipal_UsesHttpContextAccessorPrincipal()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreatePrincipal("owner")
            }
        };

        using var service = CreateService(
            new TemporaryFileOptions
            {
                StorageDirectory = _storageDirectory,
                MaxFileSizeBytes = 1024 * 1024,
                MaxTotalStorageBytes = 1024 * 1024,
                MaxFileCount = 10,
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            httpContextAccessor);

        var url = await service.StoreTemporaryFileAsync(
            new byte[] { 1, 2, 3 },
            "image/png");

        var fileId = Path.GetFileName(url);
        var ownerResult = await service.GetTemporaryFileAsync(fileId, principal: CreatePrincipal("owner"));
        var otherResult = await service.GetTemporaryFileAsync(fileId, principal: CreatePrincipal("other"));

        ownerResult.Should().NotBeNull();
        otherResult.Should().BeNull();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorage_AllowsCrossInstanceRetrieval()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var distributedCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var redis = CreateRedisLeaseMultiplexer();
        var firstService = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp"
            },
            cloudStorage,
            distributedCache,
            redis: redis);
        var secondService = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-b"),
                BaseUrl = "/temp"
            },
            cloudStorage,
            distributedCache,
            redis: redis);
        var owner = CreatePrincipal("owner");

        var url = await firstService.StoreTemporaryFileAsync(
            [1, 2, 3, 4],
            "image/png",
            principal: owner);

        Path.GetFileName(url).Should().NotContain("/");
        var retrieved = await secondService.GetTemporaryFileAsync(Path.GetFileName(url), owner);

        retrieved.Should().NotBeNull();
        retrieved!.Value.contentType.Should().Be("image/png");
        retrieved.Value.data.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorage_PreservesPrincipalAuthorization()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AzureBlob);
        var distributedCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var redis = CreateRedisLeaseMultiplexer();
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp"
            },
            cloudStorage,
            distributedCache,
            redis: redis);
        var owner = CreatePrincipal("owner");
        var other = CreatePrincipal("other");

        var url = await service.StoreTemporaryFileAsync(
            [1, 2, 3],
            "image/png",
            principal: owner);

        var fileId = Path.GetFileName(url);
        var ownerResult = await service.GetTemporaryFileAsync(fileId, owner);
        var otherResult = await service.GetTemporaryFileAsync(fileId, other);

        ownerResult.Should().NotBeNull();
        otherResult.Should().BeNull();
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorage_RemainsReadableAfterDistributedCacheEviction()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var distributedCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var redis = CreateRedisLeaseMultiplexer();
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp"
            },
            cloudStorage,
            distributedCache,
            redis: redis);

        var url = await service.StoreTemporaryFileAsync(
            [4, 5, 6],
            "image/png");

        var fileId = Path.GetFileName(url);
        await distributedCache.RemoveAsync("honua:temporary-files:cloud:" + Path.GetFileNameWithoutExtension(fileId));

        var retrieved = await service.GetTemporaryFileAsync(fileId);

        retrieved.Should().NotBeNull();
        retrieved!.Value.data.Should().Equal(4, 5, 6);
        retrieved.Value.contentType.Should().Be("image/png");
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorageAndRedisLease_SerializesQuotaAcrossInstances()
    {
        var cloudStorage = new FakeCloudFileStorage(
            CloudStorageProvider.AwsS3,
            uploadDelay: TimeSpan.FromMilliseconds(200));
        var redis = CreateRedisLeaseMultiplexer();
        var options = new TemporaryFileOptions
        {
            StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
            BaseUrl = "/temp",
            MaxFileCount = 1,
            MaxTotalStorageBytes = 1024 * 1024,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        };
        var firstService = CreateCloudAwareService(options, cloudStorage, redis: redis);
        var secondService = CreateCloudAwareService(options, cloudStorage, redis: redis);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> StoreAsync(CloudBackedTemporaryFileService service)
        {
            await start.Task.ConfigureAwait(false);

            try
            {
                await service.StoreTemporaryFileAsync([1, 2, 3], "image/png").ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var firstStore = StoreAsync(firstService);
        var secondStore = StoreAsync(secondService);

        start.SetResult();
        var results = await Task.WhenAll(firstStore, secondStore);

        results.Count(result => result is null).Should().Be(1);
        results.Count(result => result is TemporaryStorageLimitExceededException).Should().Be(1);
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithDisconnectedRedis_FailsClosedForCloudStorage()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(false);
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp",
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            cloudStorage,
            redis: redis);

        Func<Task> act = async () => await service.StoreTemporaryFileAsync([1, 2, 3], "image/png");

        var ex = await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
        ex.Which.Message.Should().Contain("coordination");
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithMissingRedis_FailsClosedForCloudStorage()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp",
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            cloudStorage);

        Func<Task> act = async () => await service.StoreTemporaryFileAsync([1, 2, 3], "image/png");

        var ex = await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
        ex.Which.Message.Should().Contain("Redis");
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WhenQuotaLeaseIsLostDuringUpload_FailsClosed()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var redis = CreateRedisLeaseLossMultiplexer();
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp",
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            cloudStorage,
            redis: redis);

        Func<Task> act = async () => await service.StoreTemporaryFileAsync([1, 2, 3], "image/png");

        var ex = await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
        ex.Which.Message.Should().Contain("coordination");
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorageCountsBytesBeyondFirstThousandObjects()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var redis = CreateRedisLeaseMultiplexer();
        for (var i = 0; i < 1001; i++)
        {
            await cloudStorage.UploadAsync(new ByteArrayUploadRequest
            {
                Content = [1],
                FileName = $"seed-{i}.bin",
                ContentType = "application/octet-stream",
                Folder = "temporary-files",
                TimeToLive = TimeSpan.FromMinutes(5)
            });
        }

        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp",
                MaxFileCount = 0,
                MaxTotalStorageBytes = 2025,
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            cloudStorage,
            redis: redis);

        Func<Task> act = async () => await service.StoreTemporaryFileAsync([2], "application/octet-stream");

        var ex = await act.Should().ThrowAsync<TemporaryStorageLimitExceededException>();
        ex.Which.Message.Should().Contain("maximum capacity");
    }

    [Fact]
    public async Task StoreTemporaryFileAsync_WithRemoteCloudStorageAndNoDistributedCache_UsesOpaqueRoundTrippableToken()
    {
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AwsS3);
        var redis = CreateRedisLeaseMultiplexer();
        var service = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Combine(_storageDirectory, "node-a"),
                BaseUrl = "/temp"
            },
            cloudStorage,
            redis: redis);

        var url = await service.StoreTemporaryFileAsync(
            [9, 8, 7],
            "image/png");

        Path.GetFileName(url).Should().NotContain("/");

        var retrieved = await service.GetTemporaryFileAsync(Path.GetFileName(url));

        retrieved.Should().NotBeNull();
        retrieved!.Value.data.Should().Equal(9, 8, 7);
        retrieved.Value.contentType.Should().Be("image/png");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
        {
            Directory.Delete(_storageDirectory, recursive: true);
        }
    }

    private FileSystemTemporaryFileService CreateService(
        TemporaryFileOptions options,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        return new FileSystemTemporaryFileService(
            Options.Create(options),
            NullLogger<FileSystemTemporaryFileService>.Instance,
            httpContextAccessor);
    }

    private CloudBackedTemporaryFileService CreateCloudAwareService(
        TemporaryFileOptions options,
        ICloudFileStorage cloudStorage,
        IDistributedCache? distributedCache = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IConnectionMultiplexer? redis = null)
    {
        var localService = CreateService(options, httpContextAccessor);
        return new CloudBackedTemporaryFileService(
            localService,
            cloudStorage,
            Options.Create(options),
            NullLogger<CloudBackedTemporaryFileService>.Instance,
            httpContextAccessor,
            distributedCache,
            redis);
    }

    private static IConnectionMultiplexer CreateRedisLeaseMultiplexer()
    {
        var ownerSync = new object();
        string? currentOwner = null;
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                lock (ownerSync)
                {
                    if (currentOwner is null)
                    {
                        currentOwner = callInfo.ArgAt<RedisValue>(1).ToString();
                        return Task.FromResult(true);
                    }

                    return Task.FromResult(false);
                }
            });
        database.LockExtendAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                lock (ownerSync)
                {
                    return Task.FromResult(string.Equals(
                        currentOwner,
                        callInfo.ArgAt<RedisValue>(1).ToString(),
                        StringComparison.Ordinal));
                }
            });
        database.LockReleaseAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                lock (ownerSync)
                {
                    var owner = callInfo.ArgAt<RedisValue>(1).ToString();
                    var released = string.Equals(currentOwner, owner, StringComparison.Ordinal);
                    if (released)
                    {
                        currentOwner = null;
                    }

                    return Task.FromResult(released);
                }
            });

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        return redis;
    }

    private static IConnectionMultiplexer CreateRedisLeaseLossMultiplexer()
    {
        var ownerSync = new object();
        string? currentOwner = null;
        var database = Substitute.For<IDatabase>();
        database.LockTakeAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                lock (ownerSync)
                {
                    if (currentOwner is null)
                    {
                        currentOwner = callInfo.ArgAt<RedisValue>(1).ToString();
                        return Task.FromResult(true);
                    }

                    return Task.FromResult(false);
                }
            });
        database.LockExtendAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(false));
        database.LockReleaseAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                lock (ownerSync)
                {
                    var owner = callInfo.ArgAt<RedisValue>(1).ToString();
                    var released = string.Equals(currentOwner, owner, StringComparison.Ordinal);
                    if (released)
                    {
                        currentOwner = null;
                    }

                    return Task.FromResult(released);
                }
            });

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.IsConnected.Returns(true);
        redis.GetDatabase().Returns(database);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        return redis;
    }

    private static ClaimsPrincipal CreatePrincipal(string subject)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subject)
        ], "test"));
    }

    private sealed class FakeCloudFileStorage : ICloudFileStorage
    {
        private readonly ConcurrentDictionary<string, CloudFile> _files = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);

        private readonly TimeSpan _uploadDelay;
        private readonly bool _completeUploads;

        public FakeCloudFileStorage(
            CloudStorageProvider provider,
            TimeSpan? uploadDelay = null,
            bool completeUploads = true)
        {
            Provider = provider;
            _uploadDelay = uploadDelay ?? TimeSpan.Zero;
            _completeUploads = completeUploads;
        }

        public CloudStorageProvider Provider { get; }

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            using var memoryStream = new MemoryStream();
            request.Content.CopyTo(memoryStream);

            return UploadAsync(
                new ByteArrayUploadRequest
                {
                    Content = memoryStream.ToArray(),
                    FileName = request.FileName,
                    ContentType = request.ContentType,
                    TimeToLive = request.TimeToLive,
                    Metadata = request.Metadata,
                    Folder = request.Folder
                },
                cancellationToken);
        }

        public async Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
        {
            if (!_completeUploads)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (_uploadDelay > TimeSpan.Zero)
            {
                await Task.Delay(_uploadDelay, cancellationToken);
            }

            var extension = Path.GetExtension(request.FileName);
            var fileId = $"{request.Folder}/{Guid.NewGuid():N}{extension}";
            var uploadedAt = DateTimeOffset.UtcNow;
            var cloudFile = new CloudFile
            {
                FileId = fileId,
                FileName = request.FileName,
                StoragePath = fileId,
                ContentType = request.ContentType,
                SizeBytes = request.Content.LongLength,
                UploadedAt = uploadedAt,
                ExpiresAt = request.TimeToLive.HasValue ? uploadedAt.Add(request.TimeToLive.Value) : null,
                Metadata = request.Metadata,
                Provider = Provider
            };

            _files[fileId] = cloudFile;
            _payloads[fileId] = request.Content;

            return UploadResult.CreateSuccess(cloudFile, TimeSpan.Zero);
        }

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UploadProgress?>(null);

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UploadProgress>>([]);

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Stream?>(_payloads.TryGetValue(fileId, out var payload)
                ? new MemoryStream(payload, writable: false)
                : null);
        }

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_payloads.TryGetValue(fileId, out var payload)
                ? payload.ToArray()
                : null);
        }

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        {
            var removedMetadata = _files.TryRemove(fileId, out _);
            var removedPayload = _payloads.TryRemove(fileId, out _);
            return Task.FromResult(removedMetadata || removedPayload);
        }

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
        {
            _files.TryGetValue(fileId, out var cloudFile);
            return Task.FromResult(cloudFile);
        }

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.ContainsKey(fileId));

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CloudFile> files = _files.Values
                .Where(file => string.IsNullOrWhiteSpace(folder) || file.StoragePath.StartsWith(folder, StringComparison.Ordinal))
                .OrderBy(file => file.UploadedAt)
                .Take(maxResults)
                .ToArray();

            return Task.FromResult(files);
        }

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>($"/download/{fileId}");

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(string fileName, string contentType, TimeSpan? expiresIn = null, string? folder = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<(string Url, string FileId)?>(null);

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var removed = 0;

            foreach (var file in _files.Values.Where(file => file.ExpiresAt.HasValue && file.ExpiresAt.Value <= now).ToArray())
            {
                if (_files.TryRemove(file.FileId, out _))
                {
                    _payloads.TryRemove(file.FileId, out _);
                    removed++;
                }
            }

            return Task.FromResult(removed);
        }
    }
}
