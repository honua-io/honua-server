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
    public async Task TryReadAsync_RecordsAccessAgainstObservedStorageGeneration()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(new CloudFile
        {
            FileId = ObjectKey,
            FileName = "1.png",
            StoragePath = ObjectKey,
            ContentType = "image/png",
            SizeBytes = 3,
            UploadedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            ETag = "etag-observed",
            Provider = CloudStorageProvider.Local
        });
        storage.DownloadBytesAsync(ObjectKey, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });
        var keyIndex = Substitute.For<ITileCacheKeyIndex>();
        keyIndex.IsEnabled.Returns(true);

        var result = await GeoServicesCloudTileCache.TryReadAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            CancellationToken.None,
            keyIndex,
            tenantScope: "tenant_a");

        result.Should().NotBeNull();
        await keyIndex.Received(1).RecordAccessIfCurrentAsync(
            ObjectKey,
            3,
            expiresAt,
            "tenant_a",
            "etag-observed",
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_SuccessfulRegeneration_RecordsWriteToClearExpiration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var storedExpiresAt = DateTimeOffset.UtcNow.AddHours(3);
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = storedExpiresAt,
                ETag = "etag-1",
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
    public async Task TryWriteAsync_CoordinatedWrite_AtomicallyRegistersUploadedGeneration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var storedExpiresAt = DateTimeOffset.UtcNow.AddHours(3);
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = storedExpiresAt,
                ETag = "etag-1",
                Provider = CloudStorageProvider.Local
            }));
        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        TileCacheWriteRegistration? registration = null;
        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<TileCacheMutationContext, Task>>(1)(
                new TileCacheMutationContext(
                    CancellationToken.None,
                    CancellationToken.None,
                    null,
                    (value, _) =>
                    {
                        registration = value;
                        return Task.FromResult(true);
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

        registration.Should().Be(new TileCacheWriteRegistration(3, storedExpiresAt, "tenant_a", "etag-1"));
        await keyIndex.DidNotReceive().RecordWriteAsync(
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_FailedUpload_DoesNotClearExpiration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
    public async Task TryWriteAsync_StaleGeneration_UsesObservedETagAndDoesNotRecordConflict()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(new CloudFile
        {
            FileId = ObjectKey,
            FileName = "1.png",
            StoragePath = ObjectKey,
            ContentType = "image/png",
            SizeBytes = 3,
            UploadedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            ETag = "etag-observed",
            Provider = CloudStorageProvider.Local
        });
        storage.UploadIfMatchAsync(
                Arg.Any<FileUploadRequest>(),
                "etag-observed",
                Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateFailure("precondition failed"));
        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<TileCacheMutationContext, Task>>(1)(
                new TileCacheMutationContext(CancellationToken.None, CancellationToken.None)));

        await GeoServicesCloudTileCache.TryWriteAsync(
            storage,
            new CloudStorageOptions { Enabled = true },
            ObjectKey,
            new byte[] { 1, 2, 3 },
            "image/png",
            "1.png",
            ImmutableDictionary<string, string>.Empty,
            CancellationToken.None,
            keyIndex);

        await storage.Received(1).UploadIfMatchAsync(
            Arg.Any<FileUploadRequest>(),
            "etag-observed",
            Arg.Any<CancellationToken>());
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
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-1",
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
        storage.DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>())
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

        await storage.Received(1).DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>());
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
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-1",
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
        storage.DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                fenceHeld.Should().BeTrue();
                callInfo.ArgAt<CancellationToken>(2).IsCancellationRequested.Should().BeFalse();
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
        await storage.Received(1).DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>());
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
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-1",
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

        await storage.DidNotReceive().DeleteIfMatchAsync(ObjectKey, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_LeaseChangesAfterStorageRollback_DoesNotRemoveNewIndexGeneration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-1",
                Provider = CloudStorageProvider.Local
            }));
        storage.DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>()).Returns(true);

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

        await storage.Received(1).DeleteIfMatchAsync(ObjectKey, "etag-1", Arg.Any<CancellationToken>());
        ownershipChecked.Should().BeTrue();
        await keyIndex.DidNotReceive().RemoveAsync(ObjectKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryWriteAsync_StaleRollback_DoesNotDeleteOrUnindexNewerStorageGeneration()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-stale",
                Provider = CloudStorageProvider.Local
            }));
        storage.DeleteIfMatchAsync(ObjectKey, "etag-stale", Arg.Any<CancellationToken>()).Returns(false);
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns(
            (CloudFile?)null,
            new CloudFile
            {
                FileId = ObjectKey,
                FileName = "1.png",
                StoragePath = ObjectKey,
                ContentType = "image/png",
                SizeBytes = 3,
                UploadedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ETag = "etag-new-owner",
                Provider = CloudStorageProvider.Local
            });

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        keyIndex.RecordWriteAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Redis commit failed.")));

        var indexRemovalAttempted = false;
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
                        indexRemovalAttempted = true;
                        return Task.FromResult(true);
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
            keyIndex);

        await storage.Received(1).DeleteIfMatchAsync(
            ObjectKey,
            "etag-stale",
            Arg.Any<CancellationToken>());
        indexRemovalAttempted.Should().BeFalse();
    }

    [UnitTest]
    public async Task TryWriteAsync_WaiterRechecksFreshObjectInsideFenceAndSkipsDuplicateUpload()
    {
        var freshFile = new CloudFile
        {
            FileId = ObjectKey,
            FileName = "1.png",
            StoragePath = ObjectKey,
            ContentType = "image/png",
            SizeBytes = 3,
            UploadedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ETag = "etag-first-writer",
            Provider = CloudStorageProvider.Local
        };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(ObjectKey, Arg.Any<CancellationToken>()).Returns((CloudFile?)null, freshFile);
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(freshFile));

        var keyIndex = Substitute.For<ITileCacheKeyIndex, ITileCacheMutationCoordinator>();
        keyIndex.IsEnabled.Returns(true);
        var mutationCoordinator = (ITileCacheMutationCoordinator)keyIndex;
        mutationCoordinator.ExecuteSerializedAsync(
                ObjectKey,
                Arg.Any<Func<TileCacheMutationContext, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<Func<TileCacheMutationContext, Task>>(1)(
                new TileCacheMutationContext(CancellationToken.None, CancellationToken.None)));

        for (var request = 0; request < 2; request++)
        {
            await GeoServicesCloudTileCache.TryWriteAsync(
                storage,
                new CloudStorageOptions { Enabled = true },
                ObjectKey,
                new byte[] { 1, 2, 3 },
                "image/png",
                "1.png",
                ImmutableDictionary<string, string>.Empty,
                CancellationToken.None,
                keyIndex);
        }

        await storage.Received(1).UploadIfMatchAsync(
            Arg.Any<FileUploadRequest>(),
            null,
            Arg.Any<CancellationToken>());
        await keyIndex.Received(1).RecordWriteAsync(
            ObjectKey,
            3,
            freshFile.ExpiresAt!.Value,
            null,
            Arg.Any<CancellationToken>());
    }
}
