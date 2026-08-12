// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Protocols.GeoServices.Tests.Source.ImageServer;

public sealed class GeoServicesCloudTileCacheTests
{
    private const string ObjectKey = "prefix/imageserver/tiles/1/webmercatorquad/default/hash/2/1/1.png";

    [UnitTest]
    public async Task TryReadAsync_ExplicitlyExpiredKey_IsCacheMissWithoutStorageRead()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var keyIndex = Substitute.For<ITileCacheKeyIndex>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.IsExpiredAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(true);

        var result = await GeoServicesCloudTileCache.TryReadAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            CancellationToken.None,
            keyIndex);

        result.Should().BeNull();
        await storage.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DownloadBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_SuccessfulRegeneration_RecordsWriteToClearExpiration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UploadResult { Success = true });
        var keyIndex = Substitute.For<ITileCacheKeyIndex>();
        keyIndex.IsEnabled.Returns(true);
        var data = new byte[] { 1, 2, 3 };

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            data,
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex);

        await keyIndex.Received(1).RecordWriteAsync(
            ObjectKey,
            data.LongLength,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_FailedUpload_DoesNotClearExpiration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateFailure("upload failed"));
        var keyIndex = Substitute.For<ITileCacheKeyIndex>();
        keyIndex.IsEnabled.Returns(true);

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex);

        await keyIndex.DidNotReceive().RecordWriteAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }
}
