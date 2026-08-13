// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for the bounded generated tile-cache expire/delete lifecycle operations (#2661):
/// the target window must remove exactly the in-bound tracked keys (storage-first for delete), leave
/// out-of-bound keys untouched, leave a failed delete tracked for a retry, and resume a partially
/// completed generation from the live index without redoing completed work.
/// </summary>
public sealed class TileCacheLifecycleExecutionTests
{
    private const string InBoundKey = "prefix/imageserver/tiles/1/webmercatorquad/default/abc123/2/1/1.png";
    private const string OtherLayerKey = "prefix/imageserver/tiles/2/webmercatorquad/default/abc123/2/1/1.png";
    private const string OtherZoomKey = "prefix/imageserver/tiles/1/webmercatorquad/default/abc123/9/1/1.png";
    private const string OtherStyleKey = "prefix/imageserver/tiles/1/webmercatorquad/night/abc123/2/1/1.png";

    [UnitTest]
    public async Task Delete_BoundedWindow_RemovesInBoundKeysAndLeavesOutOfBound()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        index.Seed(OtherLayerKey, 100);
        index.Seed(OtherZoomKey, 100);
        index.Seed(OtherStyleKey, 100);

        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                Style = "default",
                MinZoom = 0,
                MaxZoom = 5
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().BeEquivalentTo([InBoundKey]);
        index.Remaining.Should().BeEquivalentTo([OtherLayerKey, OtherZoomKey, OtherStyleKey]);
        await storage.Received(1).DeleteAsync(InBoundKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_TenantScopedWindow_DoesNotRemoveAnotherTenantsTiles()
    {
        const string tenantAKey = "prefix/imageserver/tiles/1/webmercatorquad/default/tenant-a/2/1/1.png";
        const string tenantBKey = "prefix/imageserver/tiles/1/webmercatorquad/default/tenant-b/2/1/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(tenantAKey, 100, "tenant_a");
        index.Seed(tenantBKey, 100, "tenant_b");
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage,
            schemaName: "tenant_a");

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().BeEquivalentTo([tenantAKey]);
        index.Remaining.Should().BeEquivalentTo([tenantBKey]);
        await storage.DidNotReceive().DeleteAsync(tenantBKey, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_DefaultTenantScope_MatchesEntriesWithoutSchemaRouting()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100, "public");
        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage,
            tenantId: "public");

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().ContainSingle().Which.Should().Be(InBoundKey);
    }

    [UnitTest]
    public async Task Expire_WhenSnapshottedEntryWasRemoved_DoesNotCreateOrphanMarker()
    {
        var index = new StatefulKeyIndex { RejectConditionalRemove = true };
        index.Seed(InBoundKey, 100);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "expire",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            CreateStorage());

        result.Status.Should().Be(OperationStatus.Completed);
        result.TotalTiles.Should().Be(0);
        result.SuccessfulTiles.Should().Be(0);
        index.Expired.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Delete_MaxTiles_TruncatesDeterministicWindowAndReportsWarning()
    {
        const string first = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string second = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(first, 100);
        index.Seed(second, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            MaxTiles = 1,
        }, index, storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.TotalTiles.Should().Be(1);
        result.SuccessfulTiles.Should().Be(1);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("truncated");
        index.Removed.Should().BeEquivalentTo([first]);
        index.Remaining.Should().BeEquivalentTo([second]);
    }

    [UnitTest]
    public async Task Delete_MaxTiles_RemainsCumulativeAcrossRetry()
    {
        const string first = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string second = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        const string outsideOriginalCap = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/2.png";
        var index = new StatefulKeyIndex();
        index.Seed(first, 100);
        index.Seed(second, 100);
        index.Seed(outsideOriginalCap, 100);
        var failSecond = true;
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<string>(0);
                if (failSecond && string.Equals(key, second, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.FromResult(true);
            });
        var checkpointStore = new InMemoryTileCacheGenerationCheckpointStore();
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            MaxTiles = 2,
            GenerationId = "gen-delete-cumulative-cap"
        };

        var firstAttempt = await ExecuteAsync(request, index, storage, checkpointStore);
        firstAttempt.Status.Should().Be(OperationStatus.Failed);
        failSecond = false;

        var retry = await ExecuteAsync(request, index, storage, checkpointStore);

        retry.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([first, second]);
        index.Remaining.Should().BeEquivalentTo([outsideOriginalCap]);
        await storage.DidNotReceive().DeleteAsync(outsideOriginalCap, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_CancellationAfterSuccess_PersistsCumulativeCapBeforeRetry()
    {
        const string first = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string second = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        const string outsideOriginalCap = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/2.png";
        var index = new StatefulKeyIndex();
        index.Seed(first, 100);
        index.Seed(second, 100);
        index.Seed(outsideOriginalCap, 100);
        using var cancellation = new CancellationTokenSource();
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (string.Equals(call.ArgAt<string>(0), first, StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }

                return Task.FromResult(true);
            });
        var checkpointStore = new InMemoryTileCacheGenerationCheckpointStore();
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            MaxTiles = 2,
            GenerationId = "gen-delete-cancel-cap"
        };

        var cancelled = async () => await ExecuteAsync(
            request,
            index,
            storage,
            checkpointStore,
            cancellationToken: cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await checkpointStore.LoadAsync(request.GenerationId!))!.CompletedUnitCount.Should().Be(1);

        var retry = await ExecuteAsync(request, index, storage, checkpointStore);

        retry.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([first, second]);
        index.Remaining.Should().BeEquivalentTo([outsideOriginalCap]);
    }

    [UnitTest]
    public async Task Delete_WhenBudgetReservationCannotBePersisted_DoesNotMutateStorage()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-delete-checkpoint-unavailable"
        };

        var act = async () => await ExecuteAsync(
            request,
            index,
            storage,
            new FailingCheckpointStore());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("checkpoint unavailable");
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_WhenCheckpointCannotBeRead_DoesNotMutateStorage()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-delete-checkpoint-read-unavailable"
        };

        var act = async () => await ExecuteAsync(
            request,
            index,
            storage,
            new FailingLoadCheckpointStore());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("checkpoint read unavailable");
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_WhenIndexIsDisabled_FailsInsteadOfReportingFalseSuccess()
    {
        var storage = CreateStorage();

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
            },
            NullTileCacheKeyIndex.Instance,
            storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.ErrorMessage.Should().Contain("not configured");
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_WhenStorageDeleteFails_LeavesKeyTracked()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);

        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.FailedTiles.Should().Be(1);
        // Mirrors the eviction sweep: a failed storage delete must not drop the index key.
        index.Removed.Should().BeEmpty();
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_WhenStorageReturnsFalseAndObjectStillExists_LeavesKeyTracked()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(false);
        storage.GetMetadataAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(StoredTile(InBoundKey));

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.FailedTiles.Should().Be(1);
        index.Removed.Should().BeEmpty();
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_WhenStorageReturnsFalseAndObjectIsAbsent_RemovesStaleIndexEntry()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(false);
        storage.GetMetadataAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns((CloudFile?)null);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Removed.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Delete_WhenIndexSnapshotIsUnavailable_FailsWithoutChangingStorage()
    {
        var index = new StatefulKeyIndex { SnapshotAvailable = false };
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.ErrorMessage.Should().Contain("temporarily unavailable");
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_AfterMidwayFailure_ResumesRemainingKeys()
    {
        const string key1 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string key2 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(key1, 100);
        index.Seed(key2, 100);

        var failSecond = true;
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var key = ci.ArgAt<string>(0);
                if (key == key2 && failSecond)
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.FromResult(true);
            });

        var checkpointStore = new InMemoryTileCacheGenerationCheckpointStore();
        var request = new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-resume-1"
        };

        var first = await ExecuteAsync(request, index, storage, checkpointStore);
        first.Status.Should().Be(OperationStatus.Failed);
        index.Removed.Should().BeEquivalentTo([key1]);
        index.Remaining.Should().BeEquivalentTo([key2]);

        // Fix-forward retry under the same generation id: the second key now succeeds.
        failSecond = false;
        var second = await ExecuteAsync(request, index, storage, checkpointStore);

        second.Status.Should().Be(OperationStatus.Completed);
        index.Remaining.Should().BeEmpty();
        (await checkpointStore.LoadAsync("gen-resume-1")).Should().BeNull();
    }

    [UnitTest]
    public async Task Expire_BoundedWindow_MarksKeysStaleWithoutDeletingBytes()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        index.Seed(OtherLayerKey, 100);

        var storage = CreateStorage();

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "expire",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Expired.Should().BeEquivalentTo([InBoundKey]);
        index.Remaining.Should().BeEquivalentTo([InBoundKey, OtherLayerKey]);
        // Expire retains the bytes: storage delete is never called.
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Expire_DoesNotReadExpirationBeforeIdempotentMarkerWrite()
    {
        var index = new StatefulKeyIndex { FailExpirationReads = true };
        index.Seed(InBoundKey, 100);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "expire",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            CreateStorage());

        result.Status.Should().Be(OperationStatus.Completed);
        index.ExpirationReadCount.Should().Be(0);
        index.Expired.Should().BeEquivalentTo([InBoundKey]);
    }

    [UnitTest]
    public async Task Expire_HoldsMutationFenceAgainstConcurrentWrite()
    {
        var markerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new StatefulKeyIndex
        {
            BeforeExpirationMarkerAsync = async _ =>
            {
                markerStarted.SetResult();
                await releaseMarker.Task;
            }
        };
        index.Seed(InBoundKey, 100);

        var expireTask = ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "expire",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            CreateStorage());
        await markerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var writerEntered = false;
        var writerTask = index.ExecuteSerializedAsync(
            InBoundKey,
            _ =>
            {
                writerEntered = true;
                return Task.CompletedTask;
            });
        await Task.Delay(50);
        writerEntered.Should().BeFalse();

        releaseMarker.SetResult();
        (await expireTask).Status.Should().Be(OperationStatus.Completed);
        await writerTask;
        writerEntered.Should().BeTrue();
    }

    [UnitTest]
    public async Task Expire_AfterPartialFailure_ReplaysIdempotentMarkersWithoutRecountingPriorSuccesses()
    {
        const string key1 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string key2 = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        var index = new StatefulKeyIndex { FailingExpirationKey = key2 };
        index.Seed(key1, 100);
        index.Seed(key2, 100);
        var checkpointStore = new InMemoryTileCacheGenerationCheckpointStore();
        var request = new TileOperationStartRequest
        {
            Operation = "expire",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
            GenerationId = "gen-expire-resume"
        };

        var first = await ExecuteAsync(request, index, CreateStorage(), checkpointStore);
        first.Status.Should().Be(OperationStatus.Failed);
        first.SuccessfulTiles.Should().Be(1);
        first.FailedTiles.Should().Be(1);

        index.FailingExpirationKey = null;
        var second = await ExecuteAsync(request, index, CreateStorage(), checkpointStore);

        second.Status.Should().Be(OperationStatus.Completed);
        second.TotalTiles.Should().Be(2);
        second.ProcessedTiles.Should().Be(2);
        second.SuccessfulTiles.Should().Be(2);
        second.FailedTiles.Should().Be(0);
        index.Expired.Should().BeEquivalentTo([key1, key2]);
    }

    [Theory]
    [InlineData("jpeg", "jpg")]
    [InlineData("tiff", "tif")]
    [InlineData("cog", "tif")]
    public async Task Delete_FormatAlias_MatchesCanonicalCacheExtension(string requestedFormat, string extension)
    {
        var key = $"prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/1/1.{extension}";
        var index = new StatefulKeyIndex();
        index.Seed(key, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(key, Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                Format = requestedFormat
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(1);
        index.Remaining.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Delete_UnresolvedService_MatchesNoTrackedLayers()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        index.Seed(OtherLayerKey, 100);

        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var graphProvider = new TestMetadataV2GraphProvider(new TestMetadataV2GraphBuilder().Build());

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                ServiceId = "missing-service",
                TileMatrixSetId = "WebMercatorQuad"
            },
            index,
            storage,
            graphProvider: graphProvider);

        result.Status.Should().Be(OperationStatus.Completed);
        result.SuccessfulTiles.Should().Be(0);
        index.Remaining.Should().BeEquivalentTo([InBoundKey, OtherLayerKey]);
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_ServiceFilter_UsesStorageLayerIdInsteadOfServiceLocalIndex()
    {
        const string storageLayerKey = "prefix/imageserver/tiles/42/webmercatorquad/default/abc/2/1/1.png";
        const string localIndexKey = "prefix/imageserver/tiles/7/webmercatorquad/default/abc/2/1/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(storageLayerKey, 100);
        index.Seed(localIndexKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var graphProvider = new TestMetadataV2GraphProvider(new TestMetadataV2GraphBuilder()
            .AddResource("resource", "resource")
            .AddStorageBinding("binding", "resource", "features", storageLayerId: 42)
            .AddService("service", "service")
            .AddPublication("publication", "service", "resource", layerIndex: 7, storageBindingId: "binding")
            .Build());

        var result = await ExecuteAsync(new TileOperationStartRequest
        {
            Operation = "delete",
            ServiceId = "service",
            TileMatrixSetId = "WebMercatorQuad"
        }, index, storage, graphProvider: graphProvider);

        result.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([storageLayerKey]);
        index.Remaining.Should().BeEquivalentTo([localIndexKey]);
    }

    [UnitTest]
    public async Task Delete_WhenConcurrentWriteReplacesSnapshot_LeavesFreshEntryTracked()
    {
        var index = new StatefulKeyIndex { RejectConditionalRemove = true };
        index.Seed(InBoundKey, 100);
        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad"
        }, index, storage);

        result.Status.Should().Be(OperationStatus.Failed);
        result.FailedTiles.Should().Be(1);
        index.Removed.Should().BeEmpty();
        index.Remaining.Should().BeEquivalentTo([InBoundKey]);
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Delete_HoldsMutationFenceAcrossStorageDeletion()
    {
        var index = new StatefulKeyIndex();
        index.Seed(InBoundKey, 100);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = CreateStorage();
        storage.DeleteAsync(InBoundKey, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            deleteStarted.SetResult();
            await releaseDelete.Task;
            return true;
        });

        var deleteTask = ExecuteAsync(new TileOperationStartRequest
        {
            Operation = "delete",
            LayerId = 1,
            TileMatrixSetId = "WebMercatorQuad",
        }, index, storage);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var writerEntered = false;
        var writerTask = index.ExecuteSerializedAsync(
            InBoundKey,
            _ =>
            {
                writerEntered = true;
                return Task.CompletedTask;
            });
        await Task.Delay(50);
        writerEntered.Should().BeFalse();

        releaseDelete.SetResult();
        (await deleteTask).Status.Should().Be(OperationStatus.Completed);
        await writerTask;
        writerEntered.Should().BeTrue();
    }

    [UnitTest]
    public async Task Delete_WithBbox_OnlyRemovesTilesInsideExtent()
    {
        const string inside = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/0.png";
        const string outside = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/3/3.png";
        var index = new StatefulKeyIndex();
        index.Seed(inside, 100);
        index.Seed(outside, 100);

        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                MinZoom = 2,
                MaxZoom = 2,
                // North-west quadrant only: tile (2,0,0) is inside, (2,3,3) is not.
                Bbox = [-179d, 40d, -95d, 84d]
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([inside]);
        index.Remaining.Should().BeEquivalentTo([outside]);
    }

    [UnitTest]
    public async Task Delete_WithAntimeridianBbox_RemovesOnlyDatelineTiles()
    {
        const string westernDateline = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/0/1.png";
        const string easternDateline = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/3/1.png";
        const string middle = "prefix/imageserver/tiles/1/webmercatorquad/default/abc/2/1/1.png";
        var index = new StatefulKeyIndex();
        index.Seed(westernDateline, 100);
        index.Seed(easternDateline, 100);
        index.Seed(middle, 100);

        var storage = CreateStorage();
        storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ExecuteAsync(
            new TileOperationStartRequest
            {
                Operation = "delete",
                LayerId = 1,
                TileMatrixSetId = "WebMercatorQuad",
                MinZoom = 2,
                MaxZoom = 2,
                Bbox = [170d, -80d, -170d, 80d]
            },
            index,
            storage);

        result.Status.Should().Be(OperationStatus.Completed);
        index.Removed.Should().BeEquivalentTo([westernDateline, easternDateline]);
        index.Remaining.Should().BeEquivalentTo([middle]);
    }

    private static async Task<TileOperationProgress> ExecuteAsync(
        TileOperationStartRequest request,
        ITileCacheKeyIndex keyIndex,
        ICloudFileStorage storage,
        ITileCacheGenerationCheckpointStore? checkpointStore = null,
        IMetadataV2GraphProvider? graphProvider = null,
        string? schemaName = null,
        string? tenantScope = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddSingleton(graphProvider ?? Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<ITileProvider>());
        services.AddSingleton(keyIndex);
        services.AddSingleton(storage);
        if (schemaName is not null)
        {
            var schemaContext = Substitute.For<ISchemaContext>();
            schemaContext.CurrentSchema.Returns(schemaName);
            services.AddSingleton(schemaContext);
        }

        if (tenantId is not null)
        {
            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.TenantId.Returns(tenantId);
            services.AddSingleton(tenantContext);
        }
        using var provider = services.BuildServiceProvider();

        var cacheInvalidationService = new OutputCacheInvalidationService(
            cacheStore: null,
            responseCache: null,
            metadataCache: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
            refreshCoordinator: null,
            logger: NullLogger<OutputCacheInvalidationService>.Instance);

        var core = new TileOperationExecutionCore(
            Substitute.For<IUniversalProgressStore>(),
            cacheInvalidationService,
            Options.Create(new TileOptions()),
            Options.Create(new LimitsOptions()),
            NullLogger.Instance,
            maxTilesCeiling: 100_000,
            checkpointStore);

        var started = TileOperationProgress.CreateInitial(
            Guid.NewGuid().ToString("N"),
            request.Operation,
            request.ServiceId,
            request.LayerId,
            request.TileMatrixSetId);

        return await core.ExecuteAsync(started, request, provider, cancellationToken, tenantScope);
    }

    private static CloudFile StoredTile(string key) => new()
    {
        FileId = key,
        FileName = "tile.png",
        StoragePath = key,
        ContentType = "image/png",
        SizeBytes = 100,
        UploadedAt = DateTimeOffset.UtcNow,
        ETag = $"etag:{key}",
        Provider = CloudStorageProvider.Local,
    };

    private static ICloudFileStorage CreateStorage()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => StoredTile(call.ArgAt<string>(0)));
        storage.DeleteIfMatchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call => storage.DeleteAsync(
                call.ArgAt<string>(0),
                call.ArgAt<CancellationToken>(2)));
        return storage;
    }

    private sealed class StatefulKeyIndex : ITileCacheKeyIndex, ITileCacheMutationCoordinator
    {
        private readonly ConcurrentDictionary<string, (long SizeBytes, string? TenantScope)> _entries = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _mutationFence = new(1, 1);

        public ConcurrentBag<string> Removed { get; } = [];

        public ConcurrentBag<string> Expired { get; } = [];

        public string? FailingExpirationKey { get; set; }

        public bool FailExpirationReads { get; set; }

        public int ExpirationReadCount { get; private set; }

        public Func<CancellationToken, Task>? BeforeExpirationMarkerAsync { get; set; }

        public bool SnapshotAvailable { get; set; } = true;

        public bool RejectConditionalRemove { get; set; }

        public bool IsEnabled => true;

        public IReadOnlyList<string> Remaining => [.. _entries.Keys];

        public void Seed(string key, long sizeBytes, string? tenantScope = null)
            => _entries[key] = (sizeBytes, tenantScope);

        public Task RecordAccessAsync(
            string key,
            long sizeBytes,
            DateTimeOffset? expiresAt,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
        {
            _entries[key] = (sizeBytes, tenantScope);
            return Task.CompletedTask;
        }

        public Task RecordWriteAsync(
            string key,
            long sizeBytes,
            DateTimeOffset expiresAt,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
            => RecordAccessAsync(key, sizeBytes, expiresAt, tenantScope, cancellationToken);

        public Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default)
        {
            ExpirationReadCount++;
            if (FailExpirationReads)
            {
                throw new InvalidOperationException("expiration read unavailable");
            }

            return Task.FromResult(Expired.Contains(key));
        }

        public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileCacheEntry>>(
                [.. _entries.Select(kvp => new TileCacheEntry(
                    kvp.Key,
                    kvp.Value.SizeBytes,
                    DateTimeOffset.UtcNow,
                    TenantScope: kvp.Value.TenantScope))]);

        public async Task<TileCacheIndexSnapshot> SnapshotWithStatusAsync(
            CancellationToken cancellationToken = default)
            => new(await SnapshotAsync(cancellationToken), SnapshotAvailable);

        public async Task<bool> MarkExpiredAsync(string key, CancellationToken cancellationToken = default)
        {
            if (BeforeExpirationMarkerAsync is not null)
            {
                await BeforeExpirationMarkerAsync(cancellationToken);
            }

            if (string.Equals(key, FailingExpirationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transient");
            }

            if (!_entries.ContainsKey(key) || Expired.Contains(key))
            {
                return false;
            }

            Expired.Add(key);
            return true;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_entries.TryRemove(key, out _))
            {
                Removed.Add(key);
            }

            return Task.CompletedTask;
        }

        public async Task<bool> TryRemoveAsync(
            TileCacheEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (RejectConditionalRemove)
            {
                return false;
            }

            await RemoveAsync(entry.Key, cancellationToken);
            return true;
        }

        public async Task ExecuteSerializedAsync(
            string key,
            Func<TileCacheMutationContext, Task> mutation,
            CancellationToken cancellationToken = default)
        {
            await _mutationFence.WaitAsync(cancellationToken);
            try
            {
                await mutation(new TileCacheMutationContext(
                    cancellationToken,
                    CancellationToken.None));
            }
            finally
            {
                _mutationFence.Release();
            }
        }

        public Task<bool> IsCurrentAsync(
            TileCacheEntry entry,
            CancellationToken cancellationToken = default)
            => Task.FromResult(!RejectConditionalRemove && _entries.ContainsKey(entry.Key));

        public async Task<TileCacheExpirationMarkResult> TryMarkExpiredIfCurrentAsync(
            TileCacheEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (BeforeExpirationMarkerAsync is not null)
            {
                await BeforeExpirationMarkerAsync(cancellationToken);
            }

            if (string.Equals(entry.Key, FailingExpirationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transient");
            }

            if (RejectConditionalRemove || !_entries.ContainsKey(entry.Key))
            {
                return TileCacheExpirationMarkResult.NotCurrent;
            }

            if (Expired.Contains(entry.Key))
            {
                return TileCacheExpirationMarkResult.AlreadyMarked;
            }

            Expired.Add(entry.Key);
            return TileCacheExpirationMarkResult.Added;
        }
    }

    private sealed class FailingCheckpointStore : ITileCacheGenerationCheckpointStore
    {
        public ValueTask SaveAsync(
            TileCacheGenerationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new InvalidOperationException("checkpoint unavailable"));

        public ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(
            string generationId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<TileCacheGenerationCheckpoint?>(null);

        public ValueTask<bool> DeleteAsync(
            string generationId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class FailingLoadCheckpointStore : ITileCacheGenerationCheckpointStore
    {
        public ValueTask SaveAsync(
            TileCacheGenerationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(
            string generationId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<TileCacheGenerationCheckpoint?>(
                new InvalidOperationException("checkpoint read unavailable"));

        public ValueTask<bool> DeleteAsync(
            string generationId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
