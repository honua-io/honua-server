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
        var storedExpiresAt = DateTimeOffset.UtcNow.AddHours(3);
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = storedExpiresAt,
                Provider = CloudStorageProvider.Local
            }));
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
            keyIndex,
            tenantScope: "tenant_a");

        await keyIndex.Received(1).RecordWriteAsync(
            ObjectKey,
            data.LongLength,
            storedExpiresAt,
            "tenant_a",
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
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_FailedIndexCommit_RollsBackUploadInsideMutationFence()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Provider = CloudStorageProvider.Local
            }));

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.RecordWriteAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Redis commit failed.")));

        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        var fenceHeld = false;
        var indexRemoved = false;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => ExecuteUnderFenceAsync(
                callInfo.ArgAt<Func<TileCacheMutationContext, Task>>(1)));
        storage.DeleteAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fenceHeld.Should().BeTrue();
                return Task.FromResult(true);
            });

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex,
            tenantScope: "tenant_a");

        await storage.Received(1).DeleteAsync(ObjectKey, Arg.Any<CancellationToken>());
        indexRemoved.Should().BeTrue();
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());

        async Task ExecuteUnderFenceAsync(Func<TileCacheMutationContext, Task> mutation)
        {
            fenceHeld = true;
            try
            {
                await mutation(new TileCacheMutationContext(
                    CancellationToken.None,
                    CancellationToken.None,
                    _ =>
                    {
                        fenceHeld.Should().BeTrue();
                        indexRemoved = true;
                        return Task.FromResult(true);
                    }));
            }
            finally
            {
                fenceHeld = false;
            }
        }
    }

    [UnitTest]
    public async Task TryWriteAsync_CancellationAfterUpload_RollsBackWithNonCancelledTokenInsideFence()
    {
        using var requestCancellation = new CancellationTokenSource();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Provider = CloudStorageProvider.Local
            }));

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.RecordWriteAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                requestCancellation.Cancel();
                return Task.FromCanceled(requestCancellation.Token);
            });

        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        var fenceHeld = false;
        var indexRemoved = false;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => ExecuteUnderFenceAsync(
                callInfo.ArgAt<Func<TileCacheMutationContext, Task>>(1),
                callInfo.ArgAt<CancellationToken>(2)));
        storage.DeleteAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                fenceHeld.Should().BeTrue();
                callInfo.ArgAt<CancellationToken>(1).IsCancellationRequested.Should().BeFalse();
                return Task.FromResult(true);
            });

        var act = () => GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            requestCancellation.Token,
            keyIndex,
            tenantScope: "tenant_a");

        await act.Should().ThrowAsync<OperationCanceledException>();
        await storage.Received(1).DeleteAsync(ObjectKey, Arg.Any<CancellationToken>());
        indexRemoved.Should().BeTrue();
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());

        async Task ExecuteUnderFenceAsync(
            Func<TileCacheMutationContext, Task> mutation,
            CancellationToken mutationToken)
        {
            fenceHeld = true;
            try
            {
                await mutation(new TileCacheMutationContext(
                    mutationToken,
                    CancellationToken.None,
                    token =>
                    {
                        token.IsCancellationRequested.Should().BeFalse();
                        fenceHeld.Should().BeTrue();
                        indexRemoved = true;
                        return Task.FromResult(true);
                    }));
            }
            finally
            {
                fenceHeld = false;
            }
        }
    }

    [UnitTest]
    public async Task TryWriteAsync_LeaseLostAfterUpload_DoesNotDeletePotentialNewGeneration()
    {
        using var leaseLost = new CancellationTokenSource();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Provider = CloudStorageProvider.Local
            }));

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.RecordWriteAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                leaseLost.Cancel();
                return Task.FromCanceled(leaseLost.Token);
            });

        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<Func<TileCacheMutationContext, Task>>(1)(
                new TileCacheMutationContext(leaseLost.Token, leaseLost.Token)));

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex,
            tenantScope: "tenant_a");

        await storage.DidNotReceive().DeleteAsync(ObjectKey, Arg.Any<CancellationToken>());
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_LeaseChangesAfterStorageRollback_DoesNotRemoveNewIndexGeneration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Provider = CloudStorageProvider.Local
            }));
        storage.DeleteAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(true);

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.RecordWriteAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Redis commit failed.")));

        var ownershipChecked = false;
        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<Func<TileCacheMutationContext, Task>>(1)(
                new TileCacheMutationContext(
                    CancellationToken.None,
                    CancellationToken.None,
                    _ =>
                    {
                        ownershipChecked = true;
                        return Task.FromResult(false);
                    })));

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex,
            tenantScope: "tenant_a");

        await storage.Received(1).DeleteAsync(ObjectKey, Arg.Any<CancellationToken>());
        ownershipChecked.Should().BeTrue();
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());
    }
}
