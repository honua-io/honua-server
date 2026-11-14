// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class TileCacheJobExecutorPublishTests
{
    [UnitTest]
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
                        Task.FromResult<byte[]?>(MvtTileBuilder.Canonical()),
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

    [UnitTest]
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

    [UnitTest]
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

    [UnitTest]
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

}
