// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.Core.Features.GeoETL.Services.Transforms;
using Honua.Server.Features.GeoETL;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Features.GeoETL;

/// <summary>
/// End-to-end proof that a GeoETL pipeline (GeoJSON source → reproject → Honua-layer /
/// PostGIS sink) runs through the durable substrate's worker loop
/// (<see cref="JobExecutionService"/>) and produces an execution record plus structured
/// logs. The PostGIS sink is exercised through the same connector the production wiring
/// uses; its insert path is backed by an in-memory <see cref="IFeatureSinkWriter"/> that
/// captures the reprojected geometry so the test stays fast and deterministic while
/// proving the full vertical slice.
/// </summary>
public sealed class PipelineEndToEndExecutionTests
{
    private const string InlineGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [0, 0] },
              "properties": { "name": "origin", "score": 10 } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [13.405, 52.52] },
              "properties": { "name": "berlin", "score": 90 } }
          ]
        }
        """;

    [IntegrationTest]
    public async Task GeoJsonSource_Reproject_PostgisSink_RunsThroughSubstrate()
    {
        // Arrange: the slice's real components, wired exactly as production registers them.
        var sinkWriter = new CapturingFeatureSinkWriter();
        var sources = new IPipelineSourceConnector[] { new GeoJsonSourceConnector() };
        var transforms = new IPipelineTransform[] { new ReprojectTransform(), new AttributeFilterTransform() };
        var sinks = new IPipelineSinkConnector[]
        {
            new NullPreviewSinkConnector(),
            new HonuaLayerSinkConnector(sinkWriter)
        };
        var factory = new PipelineComponentFactory(sources, transforms, sinks);
        var stageRunner = new PipelineStageRunner(factory);

        var definitionStore = new InMemoryPipelineDefinitionStore();
        var executionStore = new InMemoryPipelineExecutionStore();

        var pipeline = new PipelineDefinition
        {
            Id = "pl-e2e",
            Name = "GeoJSON to PostGIS",
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig
                    {
                        Type = GeoJsonSourceConnector.ConnectorType,
                        Options = new Dictionary<string, string> { ["inline"] = InlineGeoJson }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = ReprojectTransform.TransformType,
                        Options = new Dictionary<string, string> { ["fromSrid"] = "4326", ["toSrid"] = "3857" }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig
                    {
                        Type = HonuaLayerSinkConnector.ConnectorType,
                        Options = new Dictionary<string, string>
                        {
                            ["schema"] = "honua",
                            ["table"] = "pipeline_out",
                            ["targetSrid"] = "3857",
                            ["sourceSrid"] = "3857"
                        }
                    }
                }
            ]
        };
        await definitionStore.CreateAsync(pipeline);

        // The execution record + substrate job share one operation id, as the launcher builds them.
        var operationId = "geoetl-e2e-1";
        await executionStore.CreateAsync(new PipelineExecution
        {
            Id = operationId,
            PipelineId = pipeline.Id,
            PipelineVersion = pipeline.Version,
            ExecutionJobId = operationId,
            Status = PipelineExecutionStatus.Pending,
            BatchId = operationId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // The single ExtractTransformLoad executor, registered as production does (singleton
        // opening a scope per job). The scope resolves the slice's real components.
        var services = new ServiceCollection();
        services.AddSingleton<IPipelineDefinitionStore>(definitionStore);
        services.AddSingleton<IPipelineExecutionStore>(executionStore);
        services.AddSingleton(stageRunner);
        var provider = services.BuildServiceProvider();

        var executor = new PipelineJobExecutor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PipelineJobExecutor>.Instance);

        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = operationId,
            // Provisioning is the post-claim state ProcessJobAsync promotes to Running.
            Status = ExecutionJobStatus.Provisioning,
            CreatedAt = now,
            UpdatedAt = now,
            ClaimedBy = "worker-test",
            ClaimedAt = now,
            LastHeartbeatAt = now,
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.ExtractTransformLoad,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoetl:pl-e2e",
                RuntimeProfile = "managed",
                Parameters = new Dictionary<string, string>
                {
                    [PipelineJobExecutor.PipelineIdKey] = pipeline.Id,
                    [PipelineJobExecutor.ExecutionIdKey] = operationId,
                    [PipelineJobExecutor.DryRunKey] = "false"
                }
            }
        };

        var jobStore = new InMemoryExecutionJobStore(job);
        var jobQueue = new RecordingJobQueue();
        var logStore = new InMemoryExecutionLogStore();
        var cancellationTokens = new ExecutionJobCancellationTokens();

        // The real substrate worker loop drives our executor: claim → Running → execute → finalize.
        var service = new JobExecutionService(
            jobQueue, jobStore, [executor], cancellationTokens,
            Array.Empty<IJobTerminalCallback>(), logStore,
            NullLogger<JobExecutionService>.Instance);

        // Act: run a single claimed job through the substrate's ProcessJobAsync.
        await InvokeProcessJobAsync(service, operationId, "worker-test");

        // The pipeline execution record reflects the run.
        var execution = await executionStore.GetAsync(operationId);
        Assert.NotNull(execution);
        Assert.True(
            execution!.Status == PipelineExecutionStatus.Succeeded,
            $"Pipeline execution did not succeed. Status={execution.Status}, Error={execution.ErrorMessage}");

        // Assert: the substrate finalized the job as Succeeded.
        var finalJob = await jobStore.GetAsync(operationId);
        Assert.NotNull(finalJob);
        Assert.Equal(ExecutionJobStatus.Succeeded, finalJob!.Status);
        Assert.Equal(2, execution.FeaturesRead);
        Assert.Equal(2, execution.FeaturesWritten);

        // The PostGIS sink insert path received both features, tagged with the batch id.
        Assert.Equal("honua", sinkWriter.Schema);
        Assert.Equal("pipeline_out", sinkWriter.Table);
        Assert.Equal(operationId, sinkWriter.BatchId);
        Assert.Equal(2, sinkWriter.Inserted.Count);

        // Reprojection ran: WGS84 (13.405, 52.52) → Web Mercator is far from the lon/lat value.
        var berlin = sinkWriter.Inserted.Single(f =>
            string.Equals(f.Attributes?.GetOptionalValue("name")?.ToString(), "berlin", StringComparison.Ordinal));
        var point = (Point)berlin.Geometry!;
        Assert.Equal(3857, point.SRID);
        Assert.True(point.X > 1_000_000, $"Expected reprojected X in meters, got {point.X}.");
        Assert.True(point.Y > 6_000_000, $"Expected reprojected Y in meters, got {point.Y}.");

        // Structured execution logs captured the pipeline.* telemetry through the substrate.
        var logs = await logStore.GetLogsAsync(operationId);
        Assert.Contains(logs, e => e.Message.Contains("pipeline.started", StringComparison.Ordinal));
        Assert.Contains(logs, e => e.Message.Contains("pipeline.completed", StringComparison.Ordinal));
    }

    [IntegrationTest]
    public async Task DryRun_RoutesToNullSink_AndWritesNothingToDurableTarget()
    {
        var sinkWriter = new CapturingFeatureSinkWriter();
        var factory = new PipelineComponentFactory(
            [new GeoJsonSourceConnector()],
            [new ReprojectTransform()],
            [new NullPreviewSinkConnector(), new HonuaLayerSinkConnector(sinkWriter)]);
        var stageRunner = new PipelineStageRunner(factory);

        var pipeline = new PipelineDefinition
        {
            Id = "pl-dry",
            Name = "Dry run",
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig
                    {
                        Type = GeoJsonSourceConnector.ConnectorType,
                        Options = new Dictionary<string, string> { ["inline"] = InlineGeoJson }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig
                    {
                        Type = HonuaLayerSinkConnector.ConnectorType,
                        Options = new Dictionary<string, string>
                        {
                            ["schema"] = "honua", ["table"] = "ignored", ["targetSrid"] = "4326"
                        }
                    }
                }
            ]
        };

        var result = await stageRunner.RunAsync(pipeline, "batch-dry", dryRun: true, observer: null, CancellationToken.None);

        Assert.Equal(2, result.FeaturesRead);
        Assert.Equal(2, result.FeaturesWritten);
        // The durable PostGIS sink writer must never be touched on a dry run.
        Assert.Empty(sinkWriter.Inserted);
        Assert.False(sinkWriter.EnsureTargetCalled);
    }

    private static async Task InvokeProcessJobAsync(
        JobExecutionService service,
        string operationId,
        string workerId,
        CancellationToken stoppingToken = default)
    {
        var method = typeof(JobExecutionService).GetMethod(
            "ProcessJobAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var task = (Task)method!.Invoke(service, [operationId, workerId, stoppingToken])!;
        await task.ConfigureAwait(false);
    }

    /// <summary>
    /// In-memory stand-in for the Honua-layer / PostGIS <see cref="IFeatureSinkWriter"/>.
    /// Captures the features handed to the canonical insert path so the test can assert
    /// reprojected geometry and batch tagging without a live PostGIS instance.
    /// </summary>
    private sealed class CapturingFeatureSinkWriter : IFeatureSinkWriter
    {
        public List<IFeature> Inserted { get; } = [];
        public string? Schema { get; private set; }
        public string? Table { get; private set; }
        public string? BatchId { get; private set; }
        public bool EnsureTargetCalled { get; private set; }

        public Task EnsureTargetAsync(string schema, string table, int targetSrid, CancellationToken cancellationToken = default)
        {
            EnsureTargetCalled = true;
            Schema = schema;
            Table = table;
            return Task.CompletedTask;
        }

        public Task<long> InsertBatchAsync(
            string schema,
            string table,
            IReadOnlyList<IFeature> features,
            int sourceSrid,
            int targetSrid,
            string batchId,
            CancellationToken cancellationToken = default)
        {
            Schema = schema;
            Table = table;
            BatchId = batchId;
            foreach (var feature in features)
            {
                if (feature.Geometry is not null)
                {
                    feature.Geometry.SRID = targetSrid;
                }

                Inserted.Add(feature);
            }

            return Task.FromResult((long)features.Count);
        }
    }

    private sealed class InMemoryExecutionJobStore(params ExecutionJobRecord[] jobs) : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs =
            jobs.ToDictionary(job => job.OperationId, StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_jobs.ContainsKey(operation.OperationId))
            {
                return Task.FromResult(false);
            }

            _jobs[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(
                _jobs.Values.Where(job => !kind.HasValue || job.Spec.Kind == kind.Value).ToArray());
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }

    private sealed class InMemoryExecutionLogStore : IExecutionLogStore
    {
        private readonly ConcurrentDictionary<string, List<ExecutionLogEntry>> _logs = new(StringComparer.Ordinal);

        public Task AppendAsync(string operationId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        {
            _logs.GetOrAdd(operationId, _ => []).Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(
                _logs.TryGetValue(operationId, out var entries) ? entries.ToArray() : []);

        public Task SetRetentionAsync(string operationId, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
