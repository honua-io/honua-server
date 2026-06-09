// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Core.Tests.Features.Migration;

/// <summary>
/// Unit tests for <see cref="MigrationBatchOrchestrator"/> (issue #1253): ordering
/// from dependency edges, sequential child queueing, status roll-up, resumability
/// (skip succeeded / restart from failed), and relationship-apply wiring (#1256).
/// </summary>
public sealed class MigrationBatchOrchestratorTests
{
    [Fact]
    public async Task StartAsync_OrdersDependenciesBeforeDependents_AndQueuesFirstChild()
    {
        var (orchestrator, catalog, jobManager, _) = Build();

        // Related layer listed first but depends on the origin layer: orchestrator
        // must order the origin (layer:0) before the related (layer:1).
        var request = new MigrationBatchStartRequest
        {
            SourceKind = "arcgis-geoservices-rest",
            Layers =
            [
                new MigrationBatchLayerSpec
                {
                    SourceResourceId = "resource:x:layer:1",
                    ServiceUrl = "https://example.com/FeatureServer",
                    LayerId = 1,
                    TableName = "related",
                    DependsOn = ["resource:x:layer:0"]
                },
                new MigrationBatchLayerSpec
                {
                    SourceResourceId = "resource:x:layer:0",
                    ServiceUrl = "https://example.com/FeatureServer",
                    LayerId = 0,
                    TableName = "origin"
                }
            ]
        };

        var batch = await orchestrator.StartAsync(request);

        var children = await catalog.GetChildrenAsync(batch.BatchId);
        children.Should().HaveCount(2);
        children[0].SourceResourceId.Should().Be("resource:x:layer:0");
        children[1].SourceResourceId.Should().Be("resource:x:layer:1");

        // Sequential: only the first (origin) child is queued initially.
        children[0].Status.Should().Be(MigrationBatchChildStatus.Running);
        children[1].Status.Should().Be(MigrationBatchChildStatus.Pending);
        jobManager.Queue.Should().HaveCount(1);
    }

    [Fact]
    public async Task AdvanceAsync_WhenChildSucceeds_QueuesNextAndRollsUp()
    {
        var (orchestrator, catalog, jobManager, _) = Build();
        var batch = await orchestrator.StartAsync(NewRequest());

        // Complete the first child's import job successfully.
        var children = await catalog.GetChildrenAsync(batch.BatchId);
        await jobManager.CompleteJobAsync(children[0].JobId!, GeoservicesImportStatus.Completed, publishedLayerId: 100);

        var advanced = await orchestrator.AdvanceAsync(batch.BatchId);
        advanced!.SucceededChildren.Should().Be(1);
        advanced.Status.Should().Be(MigrationBatchRunStatus.Running);

        children = await catalog.GetChildrenAsync(batch.BatchId);
        children[0].Status.Should().Be(MigrationBatchChildStatus.Succeeded);
        children[0].PublishedLayerId.Should().Be(100);
        children[1].Status.Should().Be(MigrationBatchChildStatus.Running);
    }

    [Fact]
    public async Task AdvanceAsync_WhenAllSucceed_MarksBatchSucceeded()
    {
        var (orchestrator, catalog, jobManager, _) = Build();
        var batch = await orchestrator.StartAsync(NewRequest());

        await CompleteAllChildrenAsync(orchestrator, catalog, jobManager, batch.BatchId);

        var final = await catalog.GetAsync(batch.BatchId);
        final!.Status.Should().Be(MigrationBatchRunStatus.Succeeded);
        final.SucceededChildren.Should().Be(2);
        final.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AdvanceAsync_WhenChildFails_MarksBatchFailed_AndStopsQueueing()
    {
        var (orchestrator, catalog, jobManager, _) = Build();
        var batch = await orchestrator.StartAsync(NewRequest());

        var children = await catalog.GetChildrenAsync(batch.BatchId);
        await jobManager.CompleteJobAsync(children[0].JobId!, GeoservicesImportStatus.Failed);

        var advanced = await orchestrator.AdvanceAsync(batch.BatchId);
        advanced!.Status.Should().Be(MigrationBatchRunStatus.Failed);
        advanced.FailedChildren.Should().Be(1);

        children = await catalog.GetChildrenAsync(batch.BatchId);
        // The second child must remain pending (not queued) after a blocking failure.
        children[1].Status.Should().Be(MigrationBatchChildStatus.Pending);
    }

    [Fact]
    public async Task AdvanceAsync_AppliesRelationships_WhenAllChildrenPublishAndRequested()
    {
        var manifest = new MigrationManifestArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity { DisplayName = "Example", BaseUrl = "https://example.com" },
            Summary = new MigrationManifestSummary()
        };
        var manifestBody = System.Text.Json.JsonSerializer.Serialize(
            manifest,
            MigrationEvidencePackJsonContext.Default.MigrationManifestArtifact);

        var (orchestrator, catalog, jobManager, importService) = Build();
        var request = NewRequest() with { ManifestBody = manifestBody, ApplyRelationships = true };
        var batch = await orchestrator.StartAsync(request);

        await CompleteAllChildrenAsync(orchestrator, catalog, jobManager, batch.BatchId, publishedLayerId: 200);

        importService.ApplyRelationshipsCalls.Should().Be(1);
        importService.LastPublishedLayerMap.Should().ContainKey("resource:x:layer:0");
        var final = await catalog.GetAsync(batch.BatchId);
        final!.RelationshipsApplied.Should().BeTrue();
        final.Status.Should().Be(MigrationBatchRunStatus.Succeeded);
    }

    private static async Task CompleteAllChildrenAsync(
        MigrationBatchOrchestrator orchestrator,
        IMigrationBatchRunCatalog catalog,
        FakeJobManager jobManager,
        string batchId,
        int publishedLayerId = 100)
    {
        for (var guard = 0; guard < 10; guard++)
        {
            var children = await catalog.GetChildrenAsync(batchId);
            var running = children.FirstOrDefault(c => c.Status == MigrationBatchChildStatus.Running);
            if (running is null)
            {
                break;
            }

            await jobManager.CompleteJobAsync(running.JobId!, GeoservicesImportStatus.Completed, publishedLayerId);
            await orchestrator.AdvanceAsync(batchId);
        }
    }

    private static MigrationBatchStartRequest NewRequest() => new()
    {
        SourceKind = "arcgis-geoservices-rest",
        Layers =
        [
            new MigrationBatchLayerSpec
            {
                SourceResourceId = "resource:x:layer:0",
                ServiceUrl = "https://example.com/FeatureServer",
                LayerId = 0,
                TableName = "origin"
            },
            new MigrationBatchLayerSpec
            {
                SourceResourceId = "resource:x:layer:1",
                ServiceUrl = "https://example.com/FeatureServer",
                LayerId = 1,
                TableName = "related",
                DependsOn = ["resource:x:layer:0"]
            }
        ]
    };

    private static (MigrationBatchOrchestrator Orchestrator, IMigrationBatchRunCatalog Catalog, FakeJobManager JobManager, FakeImportService ImportService) Build()
    {
        var catalog = new InMemoryBatchCatalog();
        var jobManager = new FakeJobManager();
        var importService = new FakeImportService();

        var services = new ServiceCollection();
        services.AddSingleton<IMigrationBatchRunCatalog>(catalog);
        services.AddSingleton<IDistributedImportJobManager>(jobManager);
        services.AddSingleton<IGeoservicesImportService>(importService);
        var provider = services.BuildServiceProvider();

        var orchestrator = new MigrationBatchOrchestrator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MigrationBatchOrchestrator>.Instance);

        return (orchestrator, catalog, jobManager, importService);
    }

    private sealed class FakeImportService : IGeoservicesImportService
    {
        public int ApplyRelationshipsCalls { get; private set; }

        public IReadOnlyDictionary<string, int>? LastPublishedLayerMap { get; private set; }

        public Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
            MigrationManifestArtifact manifest,
            IReadOnlyDictionary<string, int> publishedLayerMap,
            IMetadataV2GraphStore? graphStore,
            CancellationToken cancellationToken = default)
        {
            ApplyRelationshipsCalls++;
            LastPublishedLayerMap = publishedLayerMap;
            return Task.FromResult(Array.Empty<MigrationRelationshipApplyOutcome>());
        }

        public Task<GeoservicesServiceInfo> DiscoverServiceAsync(GeoservicesDiscoveryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(GeoservicesDiscoveryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(GeoservicesImportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoservicesImportResult> ImportLayerAsync(GeoservicesImportRequest request, IProgress<GeoservicesImportProgress>? progress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeJobManager : IDistributedImportJobManager
    {
        private readonly FakeProgressStore<GeoservicesImportProgress> _progress = new();
        private readonly FakeProgressStore<GeoservicesImportRequest> _requests = new();
        private readonly FakeQueue _queue = new();

        public IDistributedJobQueueService JobQueue => _queue;

        public IDistributedLeaderElection LeaderElection => throw new NotSupportedException();

        public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore => _progress;

        public IDistributedProgressStore<GeoservicesImportRequest> RequestStore => _requests;

        public IReadOnlyList<string> Queue => _queue.Enqueued;

        public async Task CompleteJobAsync(string jobId, GeoservicesImportStatus status, int? publishedLayerId = null)
        {
            var current = await _progress.GetProgressAsync(jobId);
            var updated = (current ?? GeoservicesImportProgress.CreateInitial(jobId, "https://example.com", 0, "t")) with
            {
                Status = status,
                PublishedLayerId = publishedLayerId,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await _progress.SetProgressAsync(jobId, updated);
        }
    }

    private sealed class FakeQueue : IDistributedJobQueueService
    {
        private readonly List<string> _enqueued = [];

        public IReadOnlyList<string> Enqueued => _enqueued;

        public Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
        {
            _enqueued.Add(jobId);
            return Task.CompletedTask;
        }

        public Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task CompleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecoverInFlightAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default) => Task.FromResult<long>(_enqueued.Count);
    }

    private sealed class FakeProgressStore<TProgress> : IDistributedProgressStore<TProgress>
        where TProgress : class
    {
        private readonly ConcurrentDictionary<string, TProgress> _store = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _store[jobId] = progress;
            return Task.CompletedTask;
        }

        public Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.TryGetValue(jobId, out var value) ? value : null);

        public Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
        {
            _store.TryRemove(jobId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_store.Keys.ToList());
    }

    private sealed class InMemoryBatchCatalog : IMigrationBatchRunCatalog
    {
        private readonly ConcurrentDictionary<string, MigrationBatchRunRecord> _batches = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<MigrationBatchChildRecord>> _children = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _manifests = new(StringComparer.Ordinal);

        public Task<MigrationBatchRunRecord> CreateAsync(
            MigrationBatchRunRecord record,
            string? manifestBody,
            IReadOnlyList<MigrationBatchChildRecord> children,
            CancellationToken cancellationToken = default)
        {
            _batches[record.BatchId] = record;
            _children[record.BatchId] = children.OrderBy(c => c.Ordinal).ToList();
            if (!string.IsNullOrWhiteSpace(manifestBody))
            {
                _manifests[record.BatchId] = manifestBody;
            }

            return Task.FromResult(record);
        }

        public Task<MigrationBatchRunRecord?> GetAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult(_batches.TryGetValue(batchId, out var record) ? record : null);

        public Task<IReadOnlyList<MigrationBatchChildRecord>> GetChildrenAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MigrationBatchChildRecord>>(
                _children.TryGetValue(batchId, out var children) ? children.OrderBy(c => c.Ordinal).ToList() : []);

        public Task<string?> GetManifestBodyAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult(_manifests.TryGetValue(batchId, out var body) ? body : null);

        public Task<MigrationBatchChildRecord?> UpdateChildAsync(
            string batchId,
            int ordinal,
            MigrationBatchChildStatus status,
            string? jobId,
            int? publishedLayerId,
            string? statusNote,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (!_children.TryGetValue(batchId, out var children))
            {
                return Task.FromResult<MigrationBatchChildRecord?>(null);
            }

            var index = children.FindIndex(c => c.Ordinal == ordinal);
            if (index < 0 || children[index].Status is MigrationBatchChildStatus.Succeeded
                or MigrationBatchChildStatus.Failed
                or MigrationBatchChildStatus.Cancelled
                or MigrationBatchChildStatus.NeedsReview)
            {
                return Task.FromResult<MigrationBatchChildRecord?>(index < 0 ? null : children[index]);
            }

            var updated = children[index] with
            {
                Status = status,
                JobId = jobId ?? children[index].JobId,
                PublishedLayerId = publishedLayerId ?? children[index].PublishedLayerId,
                StatusNote = statusNote ?? children[index].StatusNote,
                UpdatedAt = updatedAt
            };
            children[index] = updated;
            return Task.FromResult<MigrationBatchChildRecord?>(updated);
        }

        public Task<MigrationBatchRunRecord?> UpdateBatchAsync(
            string batchId,
            MigrationBatchRunStatus status,
            int succeededChildren,
            int failedChildren,
            int cancelledChildren,
            DateTimeOffset? completedAt,
            bool? relationshipsApplied,
            string? statusNote,
            CancellationToken cancellationToken = default)
        {
            if (!_batches.TryGetValue(batchId, out var record) || record.Status != MigrationBatchRunStatus.Running)
            {
                return Task.FromResult(_batches.TryGetValue(batchId, out var existing) ? existing : null);
            }

            var updated = record with
            {
                Status = status,
                SucceededChildren = succeededChildren,
                FailedChildren = failedChildren,
                CancelledChildren = cancelledChildren,
                CompletedAt = completedAt ?? record.CompletedAt,
                RelationshipsApplied = relationshipsApplied ?? record.RelationshipsApplied,
                StatusNote = statusNote ?? record.StatusNote
            };
            _batches[batchId] = updated;
            return Task.FromResult<MigrationBatchRunRecord?>(updated);
        }

        public Task<IReadOnlyList<string>> GetActiveBatchIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(
                _batches.Values.Where(b => b.Status == MigrationBatchRunStatus.Running).Select(b => b.BatchId).ToList());
    }
}
