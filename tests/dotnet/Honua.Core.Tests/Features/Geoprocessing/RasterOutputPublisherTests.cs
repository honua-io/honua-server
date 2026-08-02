// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterOutputPublisherTests
{
    [Fact]
    public async Task PublishAsync_RetryUsesOneObjectAndOneAtomicRegistration()
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry();
        var publisher = new RasterOutputPublisher(store, registry);
        var request = Request(stage);

        var first = await publisher.PublishAsync(request);
        var retry = await publisher.PublishAsync(request);

        Assert.Equal(RasterOutputPublicationState.Published, first.State);
        Assert.Equal(first.Output, retry.Output);
        Assert.Equal(2, store.PublishCalls);
        Assert.Single(store.PublishedKeys);
        Assert.Equal(2, registry.RegisterCalls);
        Assert.Single(registry.Registrations);
    }

    [Fact]
    public async Task PublishAsync_RegistrationFailureNeverReturnsVisibleSuccessAndCanReplay()
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry { FailNextRegistration = true };
        var publisher = new RasterOutputPublisher(store, registry);
        var request = Request(stage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(request));
        Assert.Empty(registry.Registrations);
        Assert.Single(store.PublishedKeys);

        var replay = await publisher.PublishAsync(request);

        Assert.Equal(RasterOutputPublicationState.Published, replay.State);
        Assert.Single(registry.Registrations);
        Assert.Single(store.PublishedKeys);
    }

    [Fact]
    public async Task PublishAsync_EquivalentRehydratedRegistrationPassesStructuralIdentityCheck()
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry { RehydrateRegistration = true };
        var publisher = new RasterOutputPublisher(store, registry);

        var result = await publisher.PublishAsync(Request(stage));

        Assert.Equal(RasterOutputPublicationState.Published, result.State);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public async Task PublishAsync_PostgisTargetRejectsObjectOnlyRegistration()
    {
        var stage = RasterOutputContractTests.Stage();
        var publisher = new RasterOutputPublisher(
            new RecordingObjectStore(stage),
            new RecordingRegistry());
        var request = Request(stage) with
        {
            RegistrationTarget = new RasterOutputRegistrationTarget(
                RasterOutputRegistrationKind.PostgisRaster,
                "tenant-postgis")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(request));
    }

    [Theory]
    [InlineData(RasterOutputCompletionState.Failed)]
    [InlineData(RasterOutputCompletionState.Cancelled)]
    public async Task PublishAsync_NonSuccessDeletesStageAndDoesNotRegister(
        RasterOutputCompletionState completionState)
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry();
        var publisher = new RasterOutputPublisher(store, registry);

        var result = await publisher.PublishAsync(Request(stage) with { CompletionState = completionState });

        Assert.Equal(RasterOutputPublicationState.Suppressed, result.State);
        Assert.Null(result.Output);
        Assert.Contains(stage.ObjectKey, store.DeletedKeys);
        Assert.Empty(registry.Registrations);
        Assert.Empty(store.PublishedKeys);
    }

    [Fact]
    public async Task PublishAsync_CancelledTokenStillReturnsHiddenResultWithDeferredCleanup()
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry();
        var publisher = new RasterOutputPublisher(store, registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await publisher.PublishAsync(
            Request(stage) with { CompletionState = RasterOutputCompletionState.Cancelled },
            cancellation.Token);

        Assert.Equal(RasterOutputPublicationState.Suppressed, result.State);
        Assert.True(result.CleanupDeferred);
        Assert.Null(result.Output);
        Assert.Empty(store.DeletedKeys);
        Assert.Empty(registry.Registrations);
    }

    [Fact]
    public async Task SweepOrphansAsync_DeletesExpiredStageAndUnregisteredPublishButKeepsVisibleOutput()
    {
        var stage = RasterOutputContractTests.Stage();
        var store = new RecordingObjectStore(stage);
        var registry = new RecordingRegistry();
        var publisher = new RasterOutputPublisher(store, registry);
        store.Orphans.AddRange(
        [
            Candidate("raster/staging/job-1/attempt-0/a", RasterStoredObjectState.Staged),
            Candidate("raster/published/aa/unregistered.tif", RasterStoredObjectState.Published),
            Candidate("raster/published/bb/visible.tif", RasterStoredObjectState.Published)
        ]);
        registry.VisibleKeys.Add("raster/published/bb/visible.tif");

        var result = await publisher.SweepOrphansAsync(
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", CultureInfo.InvariantCulture),
            10);

        Assert.Equal(3, result.Inspected);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.RetainedVisible);
        Assert.Contains("raster/staging/job-1/attempt-0/a", store.DeletedKeys);
        Assert.Contains("raster/published/aa/unregistered.tif", store.DeletedKeys);
        Assert.DoesNotContain("raster/published/bb/visible.tif", store.DeletedKeys);
    }

    private static RasterOutputPublicationRequest Request(StagedRasterOutputDescriptor stage) => new()
    {
        Stage = stage,
        CompletionState = RasterOutputCompletionState.Succeeded,
        RegistrationTarget = new RasterOutputRegistrationTarget(
            RasterOutputRegistrationKind.CatalogObject,
            "tenant-default-raster-catalog"),
        PublishedAt = DateTimeOffset.Parse("2026-08-01T01:00:00Z", CultureInfo.InvariantCulture),
        RetainUntil = DateTimeOffset.Parse("2026-08-08T01:00:00Z", CultureInfo.InvariantCulture)
    };

    private static RasterStoredObject Candidate(string key, RasterStoredObjectState state) => new()
    {
        StoreReference = "gp-results",
        ObjectKey = key,
        ObjectVersion = "sha256:" + new string('a', 64),
        Content = RasterOutputContractTests.Content(32, new string('a', 64)),
        State = state,
        LastModifiedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture)
    };

    private sealed class RecordingObjectStore(StagedRasterOutputDescriptor stage) : IRasterOutputObjectStore
    {
        public int PublishCalls { get; private set; }

        public HashSet<string> PublishedKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> DeletedKeys { get; } = new(StringComparer.Ordinal);

        public List<RasterStoredObject> Orphans { get; } = [];

        public Task<RasterStoredObject> StageAsync(
            StagedRasterOutputDescriptor descriptor,
            Stream content,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RasterStoredObject?> InspectAsync(
            string storeReference,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(objectKey, stage.ObjectKey, StringComparison.Ordinal))
            {
                return Task.FromResult<RasterStoredObject?>(new RasterStoredObject
                {
                    StoreReference = stage.StoreReference,
                    ObjectKey = stage.ObjectKey,
                    ObjectVersion = "sha256:" + stage.Content.Checksum!.Value,
                    Content = stage.Content,
                    State = RasterStoredObjectState.Staged,
                    LastModifiedAt = stage.CreatedAt
                });
            }

            return Task.FromResult<RasterStoredObject?>(null);
        }

        public Task<RasterStoredObject> PublishAsync(
            RasterObjectPublicationRequest request,
            CancellationToken cancellationToken = default)
        {
            PublishCalls++;
            PublishedKeys.Add(request.DestinationObjectKey);
            return Task.FromResult(new RasterStoredObject
            {
                StoreReference = request.Stage.StoreReference,
                ObjectKey = request.DestinationObjectKey,
                ObjectVersion = "sha256:" + request.Stage.Content.Checksum!.Value,
                Content = request.Stage.Content,
                State = RasterStoredObjectState.Published,
                LastModifiedAt = request.PublishedAt
            });
        }

        public async IAsyncEnumerable<RasterStoredObject> ListExpiredAsync(
            DateTimeOffset olderThan,
            int maximumCount,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var candidate in Orphans.Take(maximumCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
                await Task.Yield();
            }
        }

        public Task DeleteAsync(
            string storeReference,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRegistry : IRasterOutputRegistry
    {
        public bool FailNextRegistration { get; set; }

        public bool RehydrateRegistration { get; set; }

        public int RegisterCalls { get; private set; }

        public Dictionary<string, RasterOutputDescriptor> Registrations { get; } = new(StringComparer.Ordinal);

        public HashSet<string> VisibleKeys { get; } = new(StringComparer.Ordinal);

        public Task<RasterOutputRegistrationResult> RegisterAtomicallyAsync(
            RasterOutputRegistrationCommand command,
            CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            if (FailNextRegistration)
            {
                FailNextRegistration = false;
                throw new InvalidOperationException("simulated transaction rollback");
            }

            if (Registrations.TryGetValue(command.IdempotencyKey, out var existing))
            {
                return Task.FromResult(new RasterOutputRegistrationResult(existing, true));
            }

            var registered = RehydrateRegistration
                ? command.PublishedObject with
                {
                    Grid = command.PublishedObject.Grid with
                    {
                        GeoTransform = command.PublishedObject.Grid.GeoTransform.ToArray()
                    },
                    Lineage = command.PublishedObject.Lineage with
                    {
                        SourceArtifactIds = command.PublishedObject.Lineage.SourceArtifactIds.ToArray()
                    }
                }
                : command.PublishedObject;
            Registrations.Add(command.IdempotencyKey, registered);
            VisibleKeys.Add(command.PublishedObject.ObjectKey);
            return Task.FromResult<RasterOutputRegistrationResult>(
                new(registered, false));
        }

        public Task<bool> IsVisibleAsync(
            string storeReference,
            string objectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VisibleKeys.Contains(objectKey));
    }
}
