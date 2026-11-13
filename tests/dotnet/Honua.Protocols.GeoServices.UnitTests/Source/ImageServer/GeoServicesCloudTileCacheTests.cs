// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Protocols.GeoServices.Tests.Source.ImageServer;

public sealed class GeoServicesCloudTileCacheTests
{
    private const string ObjectKey = "prefix/imageserver/tiles/1/webmercatorquad/default/hash/2/1/1.png";

    [UnitTest]
    public async Task TryReadAsync_FreshObject_ReturnsStoredBytesAndContentType()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(CreateFile(DateTimeOffset.UtcNow.AddHours(1), "etag-observed"));
        storage.DownloadBytesAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });

        var result = await GeoServicesCloudTileCache.TryReadAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            CancellationToken.None);

        result.Should().NotBeNull();
        var cached = result.GetValueOrDefault();
        cached.Data.Should().Equal(1, 2, 3);
        cached.ContentType.Should().Be("image/png");
    }

    [UnitTest]
    public async Task TryReadAsync_ExpiredObject_IsCacheMissWithoutDownload()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(CreateFile(DateTimeOffset.UtcNow.AddHours(-1), "etag-observed"));

        var result = await GeoServicesCloudTileCache.TryReadAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            CancellationToken.None);

        result.Should().BeNull();
        await storage.DidNotReceive().DownloadBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_StaleGeneration_ReplacesUsingObservedETag()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(CreateFile(DateTimeOffset.UtcNow.AddHours(-1), "etag-observed"));
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), "etag-observed", Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateFailure("precondition failed"));

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None);

        await storage.Received(1).UploadIfMatchAsync(
            Arg.Is<FileUploadRequest>(request => request.ObjectKeyOverride == ObjectKey),
            "etag-observed",
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_FreshObjectAlreadyCommitted_SkipsDuplicateUpload()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(CreateFile(DateTimeOffset.UtcNow.AddHours(1), "etag-first-writer"));

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None);

        await storage.DidNotReceive().UploadIfMatchAsync(
            Arg.Any<FileUploadRequest>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private static CloudFile CreateFile(DateTimeOffset expiresAt, string etag)
        => new()
        {
            FileId = ObjectKey,
            FileName = "1.png",
            StoragePath = ObjectKey,
            ContentType = "image/png",
            SizeBytes = 3,
            UploadedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = expiresAt,
            ETag = etag,
            Provider = CloudStorageProvider.Local
        };
}
