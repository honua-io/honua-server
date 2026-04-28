// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class TileOperationJobServicePublishTests
{
    [Fact]
    public async Task Publish_WhenStorageMissing_FailsWithMessage()
    {
        var stub = new StubCloudStorage();
        using var serviceProvider = BuildScope(stub, includeCloudStorage: false);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 1,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Contain("Cloud storage is not configured");
    }

    [Fact]
    public async Task Publish_SignedUrlStrategy_PopulatesDescriptorWithSignedUrl()
    {
        var stub = new StubCloudStorage();
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromDays(1)
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "world",
            LayerId = 42,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 4
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.PublishedArtifact.Should().NotBeNull();

        var descriptor = progress.PublishedArtifact!;
        descriptor.UrlStrategy.Should().Be(PMTilesUrlStrategy.SignedUrl);
        descriptor.AccessUrl.Should().StartWith("https://signed.example/");
        descriptor.AccessUrlExpiresAt.Should().NotBeNull();
        descriptor.ContentType.Should().Be("application/vnd.pmtiles");
        descriptor.SizeBytes.Should().BeGreaterThan(0);
        descriptor.MinZoom.Should().Be(0);
        descriptor.MaxZoom.Should().Be(0);
        descriptor.LayerId.Should().Be(42);
        descriptor.ServiceId.Should().Be("world");
        descriptor.TileMatrixSetId.Should().Be("WebMercatorQuad");

        descriptor.ObjectKey.Should().EndWith("/WebMercatorQuad.pmtiles");
        descriptor.ObjectKey.Should().Contain("/world/");
        descriptor.ObjectKey.Should().Contain("/42/");

        stub.LastUpload.Should().NotBeNull();
        stub.LastUpload!.TimeToLive.Should().BeNull("publish artifacts are durable, no TTL");
        stub.LastUpload.ObjectKeyOverride.Should().Be(descriptor.ObjectKey);
    }

    [Fact]
    public async Task Publish_RangeProxyStrategy_BuildsRelativeProxyUrl()
    {
        var stub = new StubCloudStorage();
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.RangeProxy
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 7,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var descriptor = (await sut.GetAsync(jobId))!.PublishedArtifact;
        descriptor.Should().NotBeNull();
        descriptor!.UrlStrategy.Should().Be(PMTilesUrlStrategy.RangeProxy);
        descriptor.AccessUrl.Should().Be($"/api/v1/tiles/pmtiles/{descriptor.ArtifactId}");
        descriptor.AccessUrlExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Publish_PublicUrlStrategy_BuildsPublicUrlFromConfig()
    {
        var stub = new StubCloudStorage();
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.PublicUrl,
            PublicBucketBaseUrl = "https://cdn.example.com/tiles"
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 9,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var descriptor = (await sut.GetAsync(jobId))!.PublishedArtifact;
        descriptor.Should().NotBeNull();
        descriptor!.UrlStrategy.Should().Be(PMTilesUrlStrategy.PublicUrl);
        descriptor.AccessUrl.Should().StartWith("https://cdn.example.com/tiles/");
        descriptor.AccessUrl.Should().EndWith(descriptor.ArtifactId);
        descriptor.AccessUrlExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Publish_PublicUrlStrategy_WithoutBaseUrl_FailsJob()
    {
        var stub = new StubCloudStorage();
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.PublicUrl,
            PublicBucketBaseUrl = null
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 9,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        stub.LastUpload.Should().BeNull("misconfigured PublicUrl must fail before upload to avoid orphan artifacts");
    }

    [Fact]
    public async Task Publish_SignedUrlFailure_RollsBackUploadedArtifact()
    {
        var stub = new StubCloudStorage(presignedUrlOverride: _ => null); // simulate provider returning no presigned URL
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromHours(1)
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 21,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Contain("Publish access URL generation failed");

        stub.LastUpload.Should().NotBeNull();
        stub.DeletedFileIds.Should().Contain(stub.LastUpload!.ObjectKeyOverride!,
            "the durable artifact must be removed when access URL generation fails");
        var orphanLookup = await stub.GetMetadataAsync(stub.LastUpload.ObjectKeyOverride!);
        orphanLookup.Should().BeNull("the orphan artifact must no longer be present in storage");
    }

    [Fact]
    public async Task Publish_AccessUrlFailure_DoesNotLeakProviderExceptionMessage()
    {
        const string sensitive = "AWS:AccessDenied User: arn:aws:iam::123456789012:user/secret-name";
        var stub = new StubCloudStorage(presignedUrlOverride: _ => throw new InvalidOperationException(sensitive));
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromHours(1)
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 31,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Be("Publish access URL generation failed.");
        progress.ErrorMessage.Should().NotContain("AWS");
        progress.ErrorMessage.Should().NotContain("arn:aws");

        // Rollback delete must still run on the just-uploaded artifact.
        stub.LastUpload.Should().NotBeNull();
        stub.DeletedFileIds.Should().Contain(stub.LastUpload!.ObjectKeyOverride!);
    }

    [Fact]
    public async Task Publish_RollbackDelete_WhenProviderReturnsFalse_StillFailsJobWithoutLeakingException()
    {
        var stub = new StubCloudStorage(
            presignedUrlOverride: _ => null,
            deleteResultOverride: _ => false);
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromHours(1)
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 22,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Be("Publish access URL generation failed.");

        // The delete attempt happened (so cleanup logic ran) even though the
        // provider reported it as a soft failure.
        stub.LastUpload.Should().NotBeNull();
        stub.DeletedFileIds.Should().Contain(stub.LastUpload!.ObjectKeyOverride!);
    }

    [Fact]
    public async Task Publish_DeterministicKey_OverwritesPreviousArtifact()
    {
        var stub = new StubCloudStorage();
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true);
        var sut = CreateSut(serviceProvider);

        var firstJobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "world",
            LayerId = 13,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });
        await sut.ProcessQueuedJobAsync(firstJobId);
        var firstDescriptor = (await sut.GetAsync(firstJobId))!.PublishedArtifact!;

        var secondJobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "world",
            LayerId = 13,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });
        await sut.ProcessQueuedJobAsync(secondJobId);
        var secondDescriptor = (await sut.GetAsync(secondJobId))!.PublishedArtifact!;

        secondDescriptor.ArtifactId.Should().Be(firstDescriptor.ArtifactId);
        secondDescriptor.ObjectKey.Should().Be(firstDescriptor.ObjectKey);
    }

    [Fact]
    public async Task Publish_ProviderKeyPrefix_IsAppliedToObjectKey()
    {
        var stub = new StubCloudStorage(provider: CloudStorageProvider.AwsS3);
        var cloudOptions = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "honua-bucket",
                Region = "us-east-1",
                KeyPrefix = "tenants/acme"
            },
            PMTilesPublish = new PMTilesPublishOptions
            {
                UrlStrategy = PMTilesUrlStrategy.SignedUrl,
                KeyPrefix = "pmtiles"
            }
        };

        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, cloudOptions: cloudOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 100,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });
        await sut.ProcessQueuedJobAsync(jobId);

        var descriptor = (await sut.GetAsync(jobId))!.PublishedArtifact!;
        descriptor.ObjectKey.Should().Be("tenants/acme/pmtiles/_/100/WebMercatorQuad.pmtiles");
        descriptor.Bucket.Should().Be("honua-bucket");
        descriptor.StorageProvider.Should().Be(CloudStorageProvider.AwsS3);
    }

    [Fact]
    public async Task Publish_AzureBlob_BucketIsContainerName()
    {
        var stub = new StubCloudStorage(provider: CloudStorageProvider.AzureBlob);
        var cloudOptions = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AzureBlob,
            AzureBlob = new AzureBlobOptions
            {
                ConnectionString = "fake",
                ContainerName = "tiles-container",
                BlobPrefix = "site/honua"
            },
            PMTilesPublish = new PMTilesPublishOptions
            {
                UrlStrategy = PMTilesUrlStrategy.SignedUrl,
                KeyPrefix = "pmtiles"
            }
        };

        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, cloudOptions: cloudOptions);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 5,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });
        await sut.ProcessQueuedJobAsync(jobId);

        var descriptor = (await sut.GetAsync(jobId))!.PublishedArtifact!;
        descriptor.Bucket.Should().Be("tiles-container");
        descriptor.ObjectKey.Should().Be("site/honua/pmtiles/_/5/WebMercatorQuad.pmtiles");
        descriptor.StorageProvider.Should().Be(CloudStorageProvider.AzureBlob);
    }

    [Fact]
    public void NormalizeRequest_RejectsUnknownOperation()
    {
        Action act = () =>
        {
            using var sp = BuildScope(new StubCloudStorage(), includeCloudStorage: true);
            var sut = CreateSut(sp);
            _ = sut.StartAsync(new TileOperationStartRequest { Operation = "explode" })
                .ConfigureAwait(false).GetAwaiter().GetResult();
        };
        act.Should().Throw<ArgumentException>()
            .WithMessage("*publish*");
    }

    private static TileOperationJobService CreateSut(ServiceProvider serviceProvider)
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        return new TileOperationJobService(
            progressStore,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            cacheInvalidationService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger<TileOperationJobService>.Instance);
    }

    private static ServiceProvider BuildScope(
        StubCloudStorage stub,
        bool includeCloudStorage,
        PMTilesPublishOptions? publishOptions = null,
        CloudStorageOptions? cloudOptions = null)
    {
        var services = new ServiceCollection();
        var layerCatalog = Substitute.For<ILayerCatalog>();
        services.AddSingleton(layerCatalog);

        var tileProvider = Substitute.For<ITileProvider>();
        tileProvider.GetMvtTileAsync(
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery?>(),
                Arg.Any<Honua.Core.Features.Tiles.TileOptions>(),
                Arg.Any<TileLimits>(),
                Arg.Any<CancellationToken>())
            .Returns([0x01, 0x02, 0x03, 0x04]);
        services.AddSingleton(tileProvider);

        if (includeCloudStorage)
        {
            services.AddSingleton<ICloudFileStorage>(stub);
        }

        var resolvedOptions = cloudOptions ?? new CloudStorageOptions
        {
            Provider = stub.Provider,
            AwsS3 = stub.Provider == CloudStorageProvider.AwsS3 ? new AwsS3Options { BucketName = "test-bucket", Region = "us-east-1" } : null,
            AzureBlob = stub.Provider == CloudStorageProvider.AzureBlob ? new AzureBlobOptions { ConnectionString = "fake", ContainerName = "test-container" } : null,
            PMTilesPublish = publishOptions ?? new PMTilesPublishOptions { UrlStrategy = PMTilesUrlStrategy.SignedUrl }
        };

        services.AddSingleton(Options.Create(resolvedOptions));

        return services.BuildServiceProvider();
    }

    private sealed class StubCloudStorage : ICloudFileStorage
    {
        private readonly Dictionary<string, CloudFile> _files = new(StringComparer.Ordinal);
        private readonly Func<string, string?>? _presignedUrlOverride;
        private readonly Func<string, bool?>? _deleteResultOverride;

        public StubCloudStorage(
            CloudStorageProvider provider = CloudStorageProvider.AwsS3,
            Func<string, string?>? presignedUrlOverride = null,
            Func<string, bool?>? deleteResultOverride = null)
        {
            Provider = provider;
            _presignedUrlOverride = presignedUrlOverride;
            _deleteResultOverride = deleteResultOverride;
        }

        public CloudStorageProvider Provider { get; }

        public FileUploadRequest? LastUpload { get; private set; }

        public List<string> DeletedFileIds { get; } = [];

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            LastUpload = request;
            var fileId = !string.IsNullOrWhiteSpace(request.ObjectKeyOverride)
                ? request.ObjectKeyOverride
                : Guid.NewGuid().ToString("N");

            // Drain content stream so the size aligns with what the writer produced.
            long size = 0;
            if (request.Content.CanSeek)
            {
                size = request.Content.Length;
                request.Content.Position = 0;
            }
            else
            {
                using var ms = new MemoryStream();
                request.Content.CopyTo(ms);
                size = ms.Length;
            }

            var cloudFile = new CloudFile
            {
                FileId = fileId,
                FileName = request.FileName,
                StoragePath = fileId,
                ContentType = request.ContentType,
                SizeBytes = size,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = request.TimeToLive.HasValue ? DateTimeOffset.UtcNow.Add(request.TimeToLive.Value) : null,
                Metadata = request.Metadata,
                Provider = Provider
            };
            _files[fileId] = cloudFile;
            return Task.FromResult(UploadResult.CreateSuccess(cloudFile, TimeSpan.Zero));
        }

        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => Task.FromResult<UploadProgress?>(null);

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UploadProgress>>([]);

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
        {
            DeletedFileIds.Add(fileId);
            if (_deleteResultOverride is not null)
            {
                var overridden = _deleteResultOverride(fileId);
                if (overridden.HasValue)
                {
                    return Task.FromResult(overridden.Value);
                }
            }

            return Task.FromResult(_files.Remove(fileId));
        }

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(fileId, out var f) ? f : null);

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.ContainsKey(fileId));

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudFile>>(_files.Values.ToArray());

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default)
        {
            if (_presignedUrlOverride is not null)
            {
                return Task.FromResult(_presignedUrlOverride(fileId));
            }

            return Task.FromResult<string?>($"https://signed.example/{fileId}?ttl={(int)(expiresIn?.TotalSeconds ?? 0)}");
        }

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(string fileName, string contentType, TimeSpan? expiresIn = null, string? folder = null, CancellationToken cancellationToken = default)
            => Task.FromResult<(string Url, string FileId)?>(null);

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class InMemoryUniversalProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _entries = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _entries[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default) where TProgress : class, IOperationProgress
        {
            return Task.FromResult<TProgress?>(_entries.TryGetValue(operationId, out var p) && p is TProgress typed ? typed : null);
        }

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryGetValue(operationId, out var p);
            return Task.FromResult(p);
        }

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _entries.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            var ids = _entries
                .Where(kvp => operationType == null || kvp.Value.Type == operationType.Value)
                .Select(kvp => kvp.Key)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default) where TProgress : class, IOperationProgress
        {
            var operations = _entries.Values
                .Where(progress => progress.Type == operationType)
                .OfType<TProgress>()
                .ToArray();
            return Task.FromResult<IReadOnlyList<TProgress>>(operations);
        }
    }
}
