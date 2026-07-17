// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Honua.Server.Tests.Features.Tiles;

/// <summary>
/// Multi-node (cross-replica) certification for the durable tile-export job lifecycle over a real
/// Redis execution substrate (issue #2689 / the remaining acceptance criterion of #2660). These
/// tests submit, poll, cancel, and retry across independent <see cref="TileExportJobService"/>
/// instances that share one Redis-backed store/queue and one artifact store, and drive the real
/// <see cref="JobExecutionService"/> worker and <see cref="JobReconciliationService"/> reconciler.
///
/// They certify that:
/// <list type="bullet">
///   <item>submission on one replica converges to status/result reads on another;</item>
///   <item>cancellation issued from a different replica than the submitter is honoured;</item>
///   <item>worker loss recovers through lease/heartbeat expiry without a duplicate terminal
///     artifact, with a second worker completing the job exactly once; and</item>
///   <item>a retry (fresh idempotency key, identical plan) reuses the completed package
///     checkpoint instead of regenerating tiles.</item>
/// </list>
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.MapServer)]
[Operation(Operations.Export)]
public sealed class TileExportDurableRecoveryRedisTests(RedisFixture redis)
{
    private const string Submitter = "user-alice";
    // Matches Honua.ServiceDefaults.HonuaTelemetry.ServiceName, the meter the control plane records against.
    private const string HonuaMeterName = "Honua";
    private static readonly IReadOnlySet<ExecutionJobKind> AcceptedKinds =
        new HashSet<ExecutionJobKind> { ExecutionJobKind.TileExport };
    private static readonly IReadOnlySet<string> AcceptedProfiles =
        new HashSet<string>(StringComparer.Ordinal) { TileExportExecutionSpecBuilder.RuntimeProfile };

    [IntegrationTest]
    public async Task Submit_OnOneReplica_StatusAndResult_ConvergeOnAnother()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-converge");
        var replicaA = harness.CreateService();
        var replicaB = harness.CreateService();

        await harness.RunWorkerAsync(async () =>
        {
            var submitted = await replicaA.SubmitAsync(plan, idempotencyKey: null, correlationId: "corr-1", Principal(), default);
            submitted.Status.Should().Be(ExecutionJobStatus.Queued);

            var terminal = await harness.WaitForStatusAsync(submitted.OperationId, ExecutionJobStatus.Succeeded);
            terminal.ArtifactReferences.Should().ContainSingle();

            // Poll and read the result from a different replica than the one that submitted.
            var scope = ScopeFor(plan);
            var status = await replicaB.GetStatusAsync(submitted.OperationId, scope, Principal(), default);
            status.Status.Should().Be(ExecutionJobStatus.Succeeded);

            var result = await replicaB.GetResultAsync(submitted.OperationId, scope, Principal(), default);
            result.DownloadUrl.Should().NotBeNullOrWhiteSpace();
            result.Format.Should().Be(TileExportPackageFormat.Tpkx);
        });

        harness.ProducerInvocations.Should().Be(1, "the package is generated exactly once");
    }

    [IntegrationTest]
    public async Task Cancel_FromDifferentReplica_QueuedJob_IsCancelledAndDequeued()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-cancel");
        var replicaA = harness.CreateService();
        var replicaB = harness.CreateService();

        // No worker is running, so the job stays queued and is cancellable from any replica.
        var submitted = await replicaA.SubmitAsync(plan, idempotencyKey: null, correlationId: null, Principal(), default);
        (await harness.Queue.GetQueueDepthAsync()).Should().BeGreaterThanOrEqualTo(1);

        await replicaB.CancelAsync(submitted.OperationId, ScopeFor(plan), Principal(), default);

        var cancelled = await harness.Store.GetAsync(submitted.OperationId);
        cancelled.Should().NotBeNull();
        cancelled!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task WorkerLoss_LeaseExpiry_Recovers_WithoutDuplicateTerminalArtifact()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-recovery");
        var replica = harness.CreateService();

        var submitted = await replica.SubmitAsync(plan, idempotencyKey: null, correlationId: null, Principal(), default);

        // Simulate a lost worker: claim the job, then stamp a stale heartbeat with a retry/heartbeat
        // policy so the reconciler treats the claim as dead and requeues it for another worker.
        var claimedId = await harness.Queue.TryClaimAsync("worker-lost", AcceptedKinds, AcceptedProfiles);
        claimedId.Should().Be(submitted.OperationId);

        var claimed = await harness.Store.GetAsync(submitted.OperationId);
        claimed.Should().NotBeNull();
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.Store.SetAsync(claimed! with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-lost",
            ClaimedAt = staleTimestamp,
            LastHeartbeatAt = staleTimestamp,
            UpdatedAt = staleTimestamp,
            CurrentPhase = "Running",
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 3,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            HeartbeatPolicy = new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromMilliseconds(50),
                Timeout = TimeSpan.FromMilliseconds(50)
            }
        });

        // The reconciler's first sweep runs on start; wait for the stale claim to be requeued.
        await harness.RunReconcilerAsync(async () =>
        {
            await harness.WaitForAsync(
                submitted.OperationId,
                job => job.Status == ExecutionJobStatus.Queued && job.ClaimedBy is null);
        });

        // A healthy worker now claims the requeued job and drives it to a single terminal success.
        await harness.RunWorkerAsync(async () =>
        {
            var terminal = await harness.WaitForStatusAsync(submitted.OperationId, ExecutionJobStatus.Succeeded);
            terminal.ArtifactReferences.Should().ContainSingle(
                "recovery must not publish a duplicate terminal artifact");
        });

        harness.ProducerInvocations.Should().Be(1, "only the surviving worker generated the package");
    }

    [IntegrationTest]
    public async Task Retry_WithFreshKey_ReusesCompletedPackageCheckpoint()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-reuse");
        var replica = harness.CreateService();

        await harness.RunWorkerAsync(async () =>
        {
            var first = await replica.SubmitAsync(plan, "reuse-key-1", null, Principal(), default);
            await harness.WaitForStatusAsync(first.OperationId, ExecutionJobStatus.Succeeded);
            harness.ProducerInvocations.Should().Be(1);

            // A distinct idempotency key mints a new job for the identical plan; retry/fix-forward must
            // reuse the content-addressed package already in storage rather than regenerate tiles.
            var second = await replica.SubmitAsync(plan, "reuse-key-2", null, Principal(), default);
            second.OperationId.Should().NotBe(first.OperationId);

            var secondTerminal = await harness.WaitForStatusAsync(second.OperationId, ExecutionJobStatus.Succeeded);
            secondTerminal.ArtifactReferences.Should().ContainSingle();
        });

        harness.ProducerInvocations.Should().Be(1, "the completed package checkpoint is reused, not regenerated");
    }

    [IntegrationTest]
    public async Task ArtifactExpiry_AfterSuccess_ProducesStableNotFound()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-expiry");
        var replicaA = harness.CreateService();
        var replicaB = harness.CreateService();
        var scope = ScopeFor(plan);

        string artifactKey = string.Empty;
        await harness.RunWorkerAsync(async () =>
        {
            var submitted = await replicaA.SubmitAsync(plan, idempotencyKey: null, correlationId: null, Principal(), default);
            var terminal = await harness.WaitForStatusAsync(submitted.OperationId, ExecutionJobStatus.Succeeded);
            artifactKey = terminal.ArtifactReferences.Single();

            // Expire the stored artifact out from under a completed job.
            harness.Storage.Expire(artifactKey);

            // The job stays terminally successful (stable status), but the result read maps the
            // missing/expired artifact to a sanitized not-found rather than minting a dead URL.
            var status = await replicaB.GetStatusAsync(submitted.OperationId, scope, Principal(), default);
            status.Status.Should().Be(ExecutionJobStatus.Succeeded);

            await FluentActions
                .Awaiting(() => replicaB.GetResultAsync(submitted.OperationId, scope, Principal(), default))
                .Should().ThrowAsync<TileExportNotFoundException>();
        });
    }

    [IntegrationTest]
    public async Task Execution_EmitsJobTransitionMetricsCarryingJobIdentifiers()
    {
        await using var harness = await TileExportRedisHarness.CreateAsync(redis.ConnectionString);
        var plan = CreatePlan("world-basemap-metrics");
        var replica = harness.CreateService();
        var transitions = new ConcurrentBag<(string Kind, string Status)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == HonuaMeterName
                    && instrument.Name == ControlPlaneTelemetry.Metrics.ExecutionJobTransitions)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? kind = null;
            string? status = null;
            foreach (var tag in tags)
            {
                if (tag.Key == ControlPlaneTelemetry.Tags.ExecutionJobKind)
                {
                    kind = tag.Value?.ToString();
                }
                else if (tag.Key == ControlPlaneTelemetry.Tags.ExecutionJobStatus)
                {
                    status = tag.Value?.ToString();
                }
            }

            if (kind is not null && status is not null)
            {
                transitions.Add((kind, status));
            }
        });
        listener.Start();

        await harness.RunWorkerAsync(async () =>
        {
            var submitted = await replica.SubmitAsync(plan, idempotencyKey: null, correlationId: null, Principal(), default);
            await harness.WaitForStatusAsync(submitted.OperationId, ExecutionJobStatus.Succeeded);
        });

        listener.Dispose();

        // The control-plane transition metric exposes the job kind and status across the run,
        // so tile-export jobs are sliceable by identity on the same substrate observability surface.
        var tileExportTransitions = transitions.Where(t => t.Kind == ExecutionJobKind.TileExport.ToString()).ToArray();
        tileExportTransitions.Should().NotBeEmpty();
        tileExportTransitions.Should().Contain(t => t.Status == ExecutionJobStatus.Succeeded.ToString());
    }

    private static TileExportJobPlan CreatePlan(string resourceId)
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = resourceId,
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                "provider-revision-9",
                null),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };

    private static TileExportJobScope ScopeFor(TileExportJobPlan plan) => new(plan.SourceKind, plan.ResourceId);

    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Submitter)], authenticationType: "test"));

    /// <summary>
    /// Owns the shared Redis substrate, artifact store, and tile-export executor for a single test,
    /// and exposes helpers to run the worker/reconciler background services for a scoped operation.
    /// </summary>
    private sealed class TileExportRedisHarness : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _multiplexer;
        private readonly IServer _server;
        private readonly IDatabase _database;
        private readonly CountingTileExportProducer _producer;
        private readonly TileExportJobExecutor _executor;
        private readonly TimeProvider _timeProvider;

        private TileExportRedisHarness(ConnectionMultiplexer multiplexer)
        {
            _multiplexer = multiplexer;
            _database = multiplexer.GetDatabase();
            _server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
            // A fixed clock keeps the content-addressed reuse horizon deterministic: the stored
            // artifact's expiry and a later job's minimum-expiry requirement evaluate at the same
            // instant, so a valid checkpoint is reused rather than marginally re-expired.
            _timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
            Store = new RedisExecutionJobStore(_multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
            LogStore = new RedisExecutionLogStore(_multiplexer, NullLogger<RedisExecutionLogStore>.Instance);
            Queue = new RedisJobQueue(_multiplexer, Store, NullLogger<RedisJobQueue>.Instance);
            Storage = new RecordingCloudFileStorage(_timeProvider);
            _producer = new CountingTileExportProducer();
            _executor = new TileExportJobExecutor(
                Storage,
                [_producer],
                [new AlwaysAvailableFence()],
                _timeProvider,
                NullLogger<TileExportJobExecutor>.Instance);
        }

        public RedisExecutionJobStore Store { get; }

        public RedisJobQueue Queue { get; }

        public RedisExecutionLogStore LogStore { get; }

        public RecordingCloudFileStorage Storage { get; }

        public int ProducerInvocations => _producer.Invocations;

        public static async Task<TileExportRedisHarness> CreateAsync(string connectionString)
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var harness = new TileExportRedisHarness(multiplexer);
            await harness.CleanupAsync();
            return harness;
        }

        public TileExportJobService CreateService()
            => new(
                TimeProvider.System,
                Options.Create(new CloudStorageOptions()),
                NullLogger<TileExportJobService>.Instance,
                Store,
                Queue,
                Storage,
                admissionEvaluator: null);

        public async Task RunWorkerAsync(Func<Task> body)
        {
            var worker = new JobExecutionService(
                Queue,
                Store,
                [_executor],
                new ExecutionJobCancellationTokens(),
                [],
                LogStore,
                NullLogger<JobExecutionService>.Instance);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await body();
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        public async Task RunReconcilerAsync(Func<Task> body)
        {
            var reconciler = new JobReconciliationService(
                Store,
                Queue,
                Queue,
                new ExecutionJobCancellationTokens(),
                [],
                LogStore,
                NullLogger<JobReconciliationService>.Instance);
            await reconciler.StartAsync(CancellationToken.None);
            try
            {
                await body();
            }
            finally
            {
                await reconciler.StopAsync(CancellationToken.None);
            }
        }

        public Task<ExecutionJobRecord> WaitForStatusAsync(string operationId, ExecutionJobStatus status)
            => WaitForAsync(operationId, job => job.Status == status);

        public async Task<ExecutionJobRecord> WaitForAsync(
            string operationId,
            Func<ExecutionJobRecord, bool> predicate,
            TimeSpan? timeout = null)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
            ExecutionJobRecord? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                last = await Store.GetAsync(operationId);
                if (last is not null && predicate(last))
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            throw new TimeoutException(
                $"Timed out waiting for tile-export job '{operationId}'. Last status: {last?.Status.ToString() ?? "<missing>"}.");
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
            await _multiplexer.DisposeAsync();
        }

        private async Task CleanupAsync()
        {
            var keys = _server.Keys(pattern: "controlplane:*").ToArray();
            if (keys.Length > 0)
            {
                await _database.KeyDeleteAsync(keys);
            }
        }
    }

    /// <summary>
    /// Deterministic tile-export producer that counts generations, so a reuse of the completed
    /// package checkpoint is observable as the absence of a second invocation.
    /// </summary>
    private sealed class CountingTileExportProducer : ITileExportPackageProducer
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public bool CanProduce(TileExportJobPlan plan) => plan.SourceKind == TileExportSourceKind.Map;

        public async Task ProduceAsync(TileExportJobPlan plan, Stream destination, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            await destination.WriteAsync(new byte[] { 0x54, 0x50, 0x4B, 0x58 }, cancellationToken);
        }
    }

    private sealed class AlwaysAvailableFence : ITileExportSourceFence
    {
        public TileExportSourceKind SourceKind => TileExportSourceKind.Map;

        public ValueTask<bool> IsAvailableAsync(TileExportJobPlan plan, CancellationToken cancellationToken)
            => ValueTask.FromResult(true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// Minimal thread-safe in-memory <see cref="ICloudFileStorage"/> that honours the object-key
    /// override, retention TTL, and identity metadata the tile-export executor relies on for
    /// content-addressed checkpoint reuse and presigned result delivery.
    /// </summary>
    private sealed class RecordingCloudFileStorage(TimeProvider timeProvider) : ICloudFileStorage
    {
        private readonly ConcurrentDictionary<string, CloudFile> _files = new(StringComparer.Ordinal);

        public CloudStorageProvider Provider => CloudStorageProvider.Local;

        public async Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var key = request.ObjectKeyOverride
                ?? throw new InvalidOperationException("Tile-export uploads must supply an ObjectKeyOverride.");

            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var now = timeProvider.GetUtcNow();
            var file = new CloudFile
            {
                FileId = key,
                FileName = request.FileName,
                StoragePath = key,
                ContentType = request.ContentType,
                SizeBytes = bytes.Length,
                UploadedAt = now,
                ExpiresAt = request.TimeToLive is { } ttl ? now.Add(ttl) : null,
                Provider = CloudStorageProvider.Local,
                Metadata = request.Metadata
            };
            _files[key] = file;
            return UploadResult.CreateSuccess(file);
        }

        /// <summary>Expires a stored artifact in place to certify the expiry-driven result response.</summary>
        public void Expire(string fileId)
        {
            if (_files.TryGetValue(fileId, out var file))
            {
                _files[fileId] = file with { ExpiresAt = timeProvider.GetUtcNow().AddMinutes(-1) };
            }
        }

        public Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(fileId, out var file) ? file : null);

        public Task<string?> GetPresignedUrlAsync(string fileId, TimeSpan? expiresIn = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_files.ContainsKey(fileId) ? $"https://signed.example/{fileId}" : null);

        public Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.ContainsKey(fileId));

        // Members the tile-export path does not exercise throw so unexpected usage is loud.
        public Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CloudFile>> ListFilesAsync(
            string? folder = null,
            int maxResults = 1000,
            bool includeMetadata = true,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
            string fileName,
            string contentType,
            TimeSpan? expiresIn = null,
            string? folder = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
