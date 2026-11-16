// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class PairedPublishEndpointTests
{
    [IntegrationTest]
    public async Task PublicationRefresh_RequiresNewCaptureForDataMetadataOrSourceChanges()
    {
        var published = await PublishAsync(IdentityField);
        var descriptor = await ReadJsonAsync<PublicationDescriptor>(published.DescriptorKey);
        var revision = long.Parse(descriptor.Capture.SourceRevision.Split(':')[0][9..], System.Globalization.CultureInfo.InvariantCulture);
        var changes = new RefreshChangeTracker();
        var source = descriptor.SourceLayer;
        var decision = new PairedPublicationRefreshDecision(changes);

        (await decision.IsCurrentAsync(descriptor, new(source, revision, 73), default)).Should().BeTrue();
        changes.Changed = true;
        (await decision.IsCurrentAsync(descriptor, new(source, revision, 73), default)).Should().BeFalse();
        changes.LastLayer.Should().Be(73);
        changes.Changed = false;
        (await decision.IsCurrentAsync(descriptor, new(source, revision + 1, 73), default)).Should().BeFalse();
        (await decision.IsCurrentAsync(descriptor, new(source with { Layer = "different" }, revision, 73), default)).Should().BeFalse();
        (await decision.IsCurrentAsync(null, new(source, revision, 73), default)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task PublicationRefresh_NewRecipe_SubmitsOnceAcrossReplicasAndActiveJobs()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var target = snapshot.Graph.Publications.First(layer => layer.ServiceId == snapshot.Index.ServicesByName[WebAppFixture.TestServiceId].Metadata.Id
            && layer.LayerIndex == WebAppFixture.TestLayerId);
        var graph = snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(layer => layer == target ? layer with
            {
                PairedPublication = new MetadataV2PairedPublication
                {
                    PublicationId = _publicationId, IdentityField = IdentityField, MaxZoom = 2, MaxTiles = 50
                }
            } : layer).ToArray()
        };
        var provider = new RefreshGraphProvider(new MetadataV2GraphSnapshot(graph, snapshot.Etag, snapshot.LoadedAt));
        var jobs = new RefreshJobs();
        var lease = new RefreshLeaseStore();
        await using var scope = _fixture.Services.CreateAsyncScope();
        PairedPublicationRefresher Worker() => new(scope.ServiceProvider, provider, Storage,
            _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>(), jobs, new RefreshChangeTracker(),
            NullLogger<PairedPublicationRefresher>.Instance, new PairedPublicationRefreshLease(lease));

        await Worker().SweepAsync(default);
        await Worker().SweepAsync(default);
        jobs.Submissions.Should().ContainSingle();
        var submission = jobs.Submissions.Single();
        submission.Request.PublicationId.Should().Be(_publicationId);
        submission.Request.IdentityField.Should().Be(IdentityField);
        submission.Request.PublicationRevision.Should().Be(snapshot.Revision);
        submission.RequestedBy.Should().Be(TileCacheJobService.SchedulerIdentityName);
        // An expired lease does not duplicate an operation that is still queued or executing.
        lease.Held = false;
        jobs.Active = [TileOperationProgress.CreateInitial("queued", "paired-publish",
            submission.Request.ServiceId, submission.Request.LayerId, null)];
        await Worker().SweepAsync(default);
        jobs.Submissions.Should().ContainSingle();

        await ExecuteQueuedPublicationAsync(submission, graph);
    }

    private async Task ExecuteQueuedPublicationAsync(TileOperationSubmission submission, MetadataV2Graph graph)
    {
        // A replacement executor reconstructs the durable job parameters, including the revision fence.
        await using var scope = _fixture.Services.CreateAsyncScope();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(graph, schema: _fixture.MetadataGraphSchema);
        var spec = TileCacheExecutionSpecBuilder.Build(submission.Request, null, new TileCacheBatchOptions());
        TileCacheExecutionSpecBuilder.TryParse(spec.Parameters, out var queued, out _, out var error).Should().BeTrue(error);
        queued.PublicationRevision.Should().Be(graph.Revision);
        using var security = Honua.Core.Features.Authorization.JobSecurityScope.BeginSystem();
        var core = ActivatorUtilities.CreateInstance<TileOperationExecutionCore>(scope.ServiceProvider, NullLogger.Instance, 100);
        var completed = await core.ExecuteAsync(TileOperationProgress.CreateInitial(Guid.NewGuid().ToString("N"),
            "paired-publish", queued.ServiceId, queued.LayerId, null), queued, scope.ServiceProvider, default);
        completed.Status.Should().Be(OperationStatus.Completed);
    }

    [IntegrationTest]
    public async Task PublicationRefresh_ValidatedGeneration_IsReusedUntilLayerChanges()
    {
        await PublishAsync(IdentityField);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var target = snapshot.Graph.Publications.First(layer => layer.ServiceId == snapshot.Index.ServicesByName[WebAppFixture.TestServiceId].Metadata.Id
            && layer.LayerIndex == WebAppFixture.TestLayerId);
        var graph = snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(layer => layer == target ? layer with
            {
                PairedPublication = new MetadataV2PairedPublication { PublicationId = _publicationId, IdentityField = IdentityField }
            } : layer).ToArray()
        };
        var provider = new RefreshGraphProvider(new MetadataV2GraphSnapshot(graph, snapshot.Etag, snapshot.LoadedAt));
        var jobs = new RefreshJobs();
        var lease = new RefreshLeaseStore();
        var changes = new RefreshChangeTracker();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var worker = new PairedPublicationRefresher(scope.ServiceProvider, provider, Storage,
            _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>(), jobs, changes,
            NullLogger<PairedPublicationRefresher>.Instance, new PairedPublicationRefreshLease(lease));

        await worker.SweepAsync(default);
        jobs.Submissions.Should().BeEmpty();
        lease.Held = false;
        changes.Changed = true;
        await worker.SweepAsync(default);
        jobs.Submissions.Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task PublicationRefresh_RetiredTarget_CannotQueueThroughAnotherPublication()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var target = snapshot.Graph.Publications.First(layer => layer.ServiceId == snapshot.Index.ServicesByName[WebAppFixture.TestServiceId].Metadata.Id
            && layer.LayerIndex == WebAppFixture.TestLayerId);
        var graph = snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(layer => layer == target ? layer with
            {
                Status = layer.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired },
                PairedPublication = new MetadataV2PairedPublication { PublicationId = _publicationId, IdentityField = IdentityField }
            } : layer).ToArray()
        };
        var provider = new RefreshGraphProvider(new MetadataV2GraphSnapshot(graph, snapshot.Etag, snapshot.LoadedAt));
        var jobs = new RefreshJobs();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var worker = new PairedPublicationRefresher(scope.ServiceProvider, provider, Storage,
            _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>(), jobs, new RefreshChangeTracker(),
            NullLogger<PairedPublicationRefresher>.Instance, new PairedPublicationRefreshLease(new RefreshLeaseStore()));

        await worker.SweepAsync(default);

        jobs.Submissions.Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task PublicationRefresh_RecipeChangesDuringSourceCapture_DoesNotQueueStaleSettings()
    {
        var current = _fixture.GetCurrentV2GraphSnapshot();
        var target = current.Graph.Publications.First(layer => layer.ServiceId == current.Index.ServicesByName[WebAppFixture.TestServiceId].Metadata.Id
            && layer.LayerIndex == WebAppFixture.TestLayerId);
        var recipe = new MetadataV2PairedPublication { PublicationId = _publicationId, IdentityField = IdentityField, MaxZoom = 2 };
        var earlier = current.Graph with
        {
            Publications = current.Graph.Publications.Select(layer => layer == target ? layer with { PairedPublication = recipe } : layer).ToArray()
        };
        var later = earlier with
        {
            Revision = earlier.Revision + 1,
            Publications = earlier.Publications.Select(layer => layer.Metadata.Id == target.Metadata.Id
                ? layer with { PairedPublication = recipe with { MaxZoom = 3 } } : layer).ToArray()
        };
        var provider = new ChangingRefreshGraphProvider(
            new MetadataV2GraphSnapshot(earlier, current.Etag, current.LoadedAt),
            new MetadataV2GraphSnapshot(later, current.Etag, current.LoadedAt));
        var jobs = new RefreshJobs();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var worker = new PairedPublicationRefresher(scope.ServiceProvider, provider, Storage,
            _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>(), jobs, new RefreshChangeTracker(),
            NullLogger<PairedPublicationRefresher>.Instance, new PairedPublicationRefreshLease(new RefreshLeaseStore()));

        await worker.SweepAsync(default);

        jobs.Submissions.Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task PublicationRefresh_MetadataChangesAfterQueue_RetainsPreviousGeneration()
    {
        var previous = await PublishAsync(IdentityField);
        var revision = _fixture.GetCurrentV2GraphSnapshot().Revision;
        var request = new TileOperationStartRequest
        {
            Operation = "paired-publish", ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
            PublicationId = _publicationId, IdentityField = IdentityField, MinZoom = 0, MaxZoom = 2, MaxTiles = 50
        };
        var spec = TileCacheExecutionSpecBuilder.Build(request, null, new TileCacheBatchOptions());
        var parameters = new Dictionary<string, string>(spec.Parameters)
        {
            ["honua.tilecache.publication_revision"] = (revision - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        TileCacheExecutionSpecBuilder.TryParse(parameters, out var queued, out _, out var error).Should().BeTrue(error);
        using var scope = _fixture.Services.CreateScope();
        using var security = Honua.Core.Features.Authorization.JobSecurityScope.BeginSystem();
        var core = ActivatorUtilities.CreateInstance<TileOperationExecutionCore>(scope.ServiceProvider, NullLogger.Instance, 100);

        var result = await core.ExecuteAsync(TileOperationProgress.CreateInitial(Guid.NewGuid().ToString("N"),
            "paired-publish", request.ServiceId, request.LayerId, null), queued, scope.ServiceProvider, default);

        result.Status.Should().Be(OperationStatus.Failed);
        (await ReadJsonAsync<PublicationPointer>(previous.PointerKey)).Version.Should().Be(previous.Version);
    }

    private sealed class ChangingRefreshGraphProvider(MetadataV2GraphSnapshot earlier, MetadataV2GraphSnapshot later) : IMetadataV2GraphProvider
    {
        private int _reads;
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(++_reads == 1 ? earlier : later);
        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(later.Revision == revision ? later : null);
    }

    private sealed class RefreshGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot.Revision == revision ? snapshot : null);
    }

    private sealed class RefreshLeaseStore : ILeaseStore
    {
        public bool Held { get; set; }
        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            var acquired = !Held;
            Held = true;
            return Task.FromResult(acquired);
        }
        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(Held);
        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RefreshJobs : ITileCacheJobService
    {
        public List<TileOperationSubmission> Submissions { get; } = [];
        public IReadOnlyList<TileOperationProgress> Active { get; set; } = [];
        public Task<string> SubmitAsync(TileOperationSubmission submission, CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return Task.FromResult("refresh-job");
        }
        public Task<IReadOnlyList<TileOperationProgress>> ListAsync(TileOperationListScope scope, CancellationToken cancellationToken = default) => Task.FromResult(Active);
        public Task<TileOperationProgress?> GetAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<TileOperationProgress?>(null);
        public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> RetryAsync(string jobId, string? requestedBy, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class RefreshChangeTracker : IChangeTracker
    {
        public bool Changed { get; set; }
        public int LastLayer { get; private set; }
        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(long sinceGeneration, int[] layerIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>([]);
        public Task<bool> HasLayerChangedSinceAsync(long sinceGeneration, int layerId, CancellationToken cancellationToken = default)
        {
            LastLayer = layerId;
            return Task.FromResult(Changed);
        }
    }
}
