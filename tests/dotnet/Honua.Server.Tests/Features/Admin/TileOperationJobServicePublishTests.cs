// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class TileOperationJobServicePublishTests
{
    [Theory]
    [InlineData("archive", true, 7)]
    [InlineData("publish", true, 7)]
    [InlineData("archive", true, null)]
    [InlineData("publish", true, null)]
    [InlineData("archive", false, 7)]
    [InlineData("publish", false, 7)]
    public async Task ArchiveOrPublish_SharedStorageLayer_UsesRequestedServiceResourceMetadata(
        string operation, bool serviceScoped, int? publicationLayerIndex)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("other", "Other resource", fields:
                [new MetadataV2Field { Name = "other_field", Type = MetadataV2FieldType.Integer }])
            .AddStorageBinding("other-binding", "other", "features", storageLayerId: 7)
            .AddService("requested-service", "requested")
            .AddResource("requested", "Requested resource", fields:
                [new MetadataV2Field { Name = "requested_field", Type = MetadataV2FieldType.String }])
            .AddStorageBinding("requested-binding", "requested", "features", storageLayerId: 7)
            .AddPublication("requested-publication", "requested-service", "requested",
                layerIndex: publicationLayerIndex, storageBindingId: "requested-binding")
            .Build();
        var stub = new StubCloudStorage();
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, graph: graph);
        var sut = CreateSut(serviceProvider);
        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = serviceScoped ? "requested" : null,
            LayerId = 7,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress!.Status.Should().Be(OperationStatus.Completed, progress.ErrorMessage);
        stub.LastUploadBytes.Should().NotBeNull();
        var header = PMTilesHeader.ReadFrom(stub.LastUploadBytes!);
        using var metadataBytes = new MemoryStream(stub.LastUploadBytes!,
            checked((int)header.JsonMetadataOffset), checked((int)header.JsonMetadataLength));
        using var decompressed = new GZipStream(metadataBytes, CompressionMode.Decompress);
        using var metadata = await JsonDocument.ParseAsync(decompressed);
        metadata.RootElement.GetProperty("name").GetString().Should()
            .Be(serviceScoped ? "Requested resource" : "Other resource");
        var fields = metadata.RootElement.GetProperty("vector_layers")[0].GetProperty("fields");
        fields.EnumerateObject().Should().ContainSingle();
        fields.GetProperty(serviceScoped ? "requested_field" : "other_field").GetString()
            .Should().Be(serviceScoped ? "string" : "integer");
    }

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
    public async Task Archive_MaxTiles_ReportsTruncationAndStampsActualCoverage()
    {
        var stub = new StubCloudStorage();
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "archive",
            LayerId = 1,
            MinZoom = 0,
            MaxZoom = 3,
            MaxTiles = 2
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.Warnings.Should().ContainSingle(w => w.Contains("exceeded maxTiles=2"));
        stub.LastUploadBytes.Should().NotBeNull();

        var header = PMTilesHeader.ReadFrom(stub.LastUploadBytes!);
        header.TileEntriesCount.Should().Be(2);
        header.MinZoom.Should().Be(0);
        header.MaxZoom.Should().Be(1, "the selected two tiles only reach zoom 1");
    }

    [Fact]
    public async Task Publish_MaxTiles_ReportsTruncationAndStampsActualCoverage()
    {
        var stub = new StubCloudStorage();
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true);
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            LayerId = 1,
            MinZoom = 0,
            MaxZoom = 3,
            MaxTiles = 2
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.Warnings.Should().ContainSingle(w => w.Contains("exceeded maxTiles=2"));
        progress.PublishedArtifact.Should().NotBeNull();
        progress.PublishedArtifact!.MaxZoom.Should().Be(1);
        PMTilesHeader.ReadFrom(stub.LastUploadBytes!).MaxZoom.Should().Be(1);
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
    public async Task Publish_AccessUrlFailure_OnRepublish_PreservesPriorArtifactAtSameKey()
    {
        var presignCalls = 0;
        var stub = new StubCloudStorage(presignedUrlOverride: fileId =>
        {
            presignCalls++;
            // First call (initial publish) succeeds; second (re-publish) returns null to
            // simulate a transient SignedUrl generation failure after the upload overwrote
            // the prior good artifact.
            return presignCalls == 1
                ? $"https://signed.example/{fileId}?ttl=3600"
                : null;
        });
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromHours(1)
        };
        using var serviceProvider = BuildScope(stub, includeCloudStorage: true, publishOptions: publishOptions);
        var sut = CreateSut(serviceProvider);

        var request = new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "world",
            LayerId = 7,
            MinZoom = 0,
            MaxZoom = 0,
            MaxTiles = 1
        };

        var firstJobId = await sut.StartAsync(request);
        await sut.ProcessQueuedJobAsync(firstJobId);
        var firstProgress = await sut.GetAsync(firstJobId);
        firstProgress!.Status.Should().Be(OperationStatus.Completed);
        var deterministicKey = firstProgress.PublishedArtifact!.ObjectKey;

        // Second publish hits the same deterministic key, but URL generation fails.
        var secondJobId = await sut.StartAsync(request);
        await sut.ProcessQueuedJobAsync(secondJobId);
        var secondProgress = await sut.GetAsync(secondJobId);
        secondProgress!.Status.Should().Be(OperationStatus.Failed);
        secondProgress.ErrorMessage.Should().Be("Publish access URL generation failed.");

        stub.DeletedFileIds.Should().NotContain(deterministicKey,
            "rollback must not delete a key that already had a previously published artifact, otherwise a transient URL failure causes data loss for clients");

        var stillExists = await stub.GetMetadataAsync(deterministicKey);
        stillExists.Should().NotBeNull("the artifact bytes at the deterministic key must remain so clients still see PMTiles content while the operator addresses the URL failure");
    }

    [Fact]
    public async Task Publish_PartialTileFailure_DoesNotUploadOrOverwriteDeterministicKey()
    {
        // Background: BuildPMTilesArchiveAsync swallows per-tile exceptions and
        // continues with whatever tiles succeeded. For the temporary `archive`
        // path that is fine — the result writes to a random per-job key with a
        // 24h TTL. For durable `publish`, the deterministic key may already
        // host a previously good artifact, so a partial generation must not be
        // promoted there. ExecutePublishAsync gates the upload on
        // build.Failed == 0; this test confirms the gate by configuring the
        // tile provider to throw on the second of two requested tiles.

        var stub = new StubCloudStorage();
        var publishOptions = new PMTilesPublishOptions
        {
            UrlStrategy = PMTilesUrlStrategy.SignedUrl,
            SignedUrlLifetime = TimeSpan.FromHours(1)
        };

        using var serviceProvider = BuildScope(
            stub,
            includeCloudStorage: true,
            publishOptions: publishOptions,
            configureTileProvider: tileProvider =>
            {
                tileProvider.GetMvtTileAsync(
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery?>(),
                        Arg.Any<Honua.Core.Features.Tiles.TileOptions>(),
                        Arg.Any<TileLimits>(),
                        Arg.Any<Honua.Core.Features.Tiles.GridGeometry?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<byte[]?>([0x01, 0x02, 0x03, 0x04]),
                        Task.FromException<byte[]?>(new InvalidOperationException("simulated tile fetch failure")));
            });
        var sut = CreateSut(serviceProvider);

        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "publish",
            ServiceId = "world",
            LayerId = 41,
            MinZoom = 0,
            MaxZoom = 1,
            MaxTiles = 5
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(OperationStatus.Failed);
        progress.ErrorMessage.Should().Contain("Publish aborted before upload");
        progress.ErrorMessage.Should().Contain("tiles failed during generation");

        progress.PublishedArtifact.Should().BeNull(
            "a partial publish must never expose a descriptor; clients would otherwise see a mix of stale and new bytes via the deterministic key");
        stub.LastUpload.Should().BeNull(
            "the partial archive must not be uploaded — that would silently overwrite the prior good artifact at the deterministic key");
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
    public async Task NormalizeRequest_RejectsUnknownOperation()
    {
        Func<Task> act = async () =>
        {
            using var sp = BuildScope(new StubCloudStorage(), includeCloudStorage: true);
            var sut = CreateSut(sp);
            _ = await sut.StartAsync(new TileOperationStartRequest { Operation = "explode" });
        };
        await act.Should().ThrowAsync<ArgumentException>()
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
        CloudStorageOptions? cloudOptions = null,
        Action<ITileProvider>? configureTileProvider = null,
        MetadataV2Graph? graph = null)
    {
        var services = new ServiceCollection();
        graph ??= new TestMetadataV2GraphBuilder()
            .AddService("svc-publish-test", "publish-test")
            .AddResource("res-publish", "publish-layer")
            .AddPublication("pub-publish", "svc-publish-test", "res-publish", layerIndex: 7)
            .AddService("world", "world")
            .AddResource("world-42", "world-42")
            .AddPublication("world-pub-42", "world", "world-42", layerIndex: 42)
            .AddPublication("world-pub-7", "world", "res-publish", layerIndex: 7)
            .AddResource("world-41", "world-41")
            .AddPublication("world-pub-41", "world", "world-41", layerIndex: 41)
            .AddResource("world-13", "world-13")
            .AddPublication("world-pub-13", "world", "world-13", layerIndex: 13)
            .Build();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));

        var tileProvider = Substitute.For<ITileProvider>();
        if (configureTileProvider is not null)
        {
            configureTileProvider(tileProvider);
        }
        else
        {
            tileProvider.GetMvtTileAsync(
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery?>(),
                    Arg.Any<Honua.Core.Features.Tiles.TileOptions>(),
                    Arg.Any<TileLimits>(),
                    Arg.Any<Honua.Core.Features.Tiles.GridGeometry?>(),
                    Arg.Any<CancellationToken>())
                .Returns([0x01, 0x02, 0x03, 0x04]);
        }
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

        public byte[]? LastUploadBytes { get; private set; }

        public List<string> DeletedFileIds { get; } = [];

        public Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            LastUpload = request;
            var fileId = !string.IsNullOrWhiteSpace(request.ObjectKeyOverride)
                ? request.ObjectKeyOverride
                : Guid.NewGuid().ToString("N");

            // Drain content stream so the size aligns with what the writer produced. Keep the
            // bytes for archive-header assertions in the PMTiles regression tests.
            using var uploadBytes = new MemoryStream();
            if (request.Content.CanSeek)
            {
                request.Content.Position = 0;
            }
            request.Content.CopyTo(uploadBytes);
            LastUploadBytes = uploadBytes.ToArray();
            var size = LastUploadBytes.LongLength;

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

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(string? folder = null, int maxResults = 1000, bool includeMetadata = true, CancellationToken cancellationToken = default)
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

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (!_entries.TryGetValue(operationId, out var current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.NotFound);
                }

                if (current.Status != expectedStatus)
                {
                    return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
                }

                if (_entries.TryUpdate(operationId, progress, current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.Updated);
                }
            }
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
