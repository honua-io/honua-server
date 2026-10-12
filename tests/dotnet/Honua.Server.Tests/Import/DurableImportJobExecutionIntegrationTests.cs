// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import.FileImport.Durable;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Import;

/// <summary>
/// End-to-end durable file import (honua-server#5916) over real Redis (execution job store and
/// queue) and real PostGIS: the accepting host stages the source in file storage and enqueues a
/// <see cref="ExecutionJobKind.FileImport"/> job; the worker downloads it, imports every feature
/// and the job document reports the runner. Covers the in-process worker loop (local backend) and
/// the execution-worker composition a batch backend launches (assigned queue, one operation).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class DurableImportJobExecutionIntegrationTests : IAsyncLifetime
{
    private const int FeatureCount = 250;
    private const string InstanceId = "durable-import-test-instance";

    private readonly WebAppFixture _fixture = new();
    private RedisContainer _redis = null!;
    private ConnectionMultiplexer _multiplexer = null!;
    private RedisExecutionJobStore _jobStore = null!;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7.2-alpine").Build();
        await Task.WhenAll(_fixture.InitializeAsync(), _redis.StartAsync());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _jobStore = new RedisExecutionJobStore(_multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _redis.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task LocalDurableRunner_InProcessWorkerLoop_CompletesImportAndReportsRunner()
    {
        var queue = new RedisJobQueue(_multiplexer, _jobStore, NullLogger<RedisJobQueue>.Instance);
        var service = CreateDispatcher(BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false), [], queue);
        var tableName = $"durable_local_{Guid.NewGuid():N}"[..28];

        var jobId = await service.QueueImportAsync(await StageAsync(tableName), 0);

        var worker = CreateWorkerLoop(queue);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForTerminalAsync(jobId, TimeSpan.FromSeconds(90));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        await AssertCompletedAsync(service, jobId, tableName, ImportJobRunners.InProcess);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task BatchRunner_ExecutionWorkerComposition_RunsTheLaunchedImportAndReportsBatch()
    {
        var backend = new LaunchRecordingBackend();
        var workload = new ExecutionJobDefinition
        {
            WorkloadId = "gp-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = backend.BackendName,
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "gp-batch",
        };
        var service = CreateDispatcher(BackgroundImportRunnerDecision.Decide(true, workload, frozenBetweenRequests: true), [backend], null);
        var tableName = $"durable_batch_{Guid.NewGuid():N}"[..28];

        var jobId = await service.QueueImportAsync(await StageAsync(tableName), 0);
        backend.Launched.Should().ContainSingle().Which.Should().Be(jobId);

        // The batch backend launches the generic server image with HONUA_OPERATION_ID; the
        // execution-worker profile feeds the shared loop from an assigned queue for that one job.
        var launch = new ExecutionWorkerLaunch
        {
            OperationId = jobId,
            JobKind = ExecutionJobKind.FileImport,
            ExecutionAttempt = 1,
        };
        var assigned = new AssignedExecutionJobQueue(launch, _jobStore, NullLogger<AssignedExecutionJobQueue>.Instance);
        var worker = CreateWorkerLoop(assigned);
        worker.RequeueOnShutdown = true;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var outcome = await assigned.Completion.WaitAsync(TimeSpan.FromSeconds(90));
            outcome.Should().Be(ExecutionWorkerClaimOutcome.Executed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        (await _jobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await AssertCompletedAsync(service, jobId, tableName, ImportJobRunners.Batch);
    }

    private DurableImportJobService CreateDispatcher(
        BackgroundImportRunnerDecision decision,
        IEnumerable<IBatchComputeBackend> backends,
        IJobQueue? queue)
        => new(
            _fixture.GetService<IImportJobService>(),
            decision,
            _fixture.GetService<IUniversalProgressStore>(),
            _fixture.GetService<IServiceScopeFactory>(),
            backends,
            NullLogger<DurableImportJobService>.Instance,
            _jobStore,
            queue,
            Identity());

    private JobExecutionService CreateWorkerLoop(IJobQueue queue)
        => new(
            queue,
            _jobStore,
            [new FileImportJobExecutor(
                _fixture.GetService<IServiceScopeFactory>(),
                _fixture.GetService<IUniversalProgressStore>(),
                NullLogger<FileImportJobExecutor>.Instance,
                Identity())],
            new ExecutionJobCancellationTokens(),
            [],
            new RedisExecutionLogStore(_multiplexer, NullLogger<RedisExecutionLogStore>.Instance),
            NullLogger<JobExecutionService>.Instance);

    private static IInstanceIdentity Identity()
    {
        var identity = Substitute.For<IInstanceIdentity>();
        identity.InstanceId.Returns(InstanceId);
        return identity;
    }

    private async Task<ImportRequest> StageAsync(string tableName)
    {
        var builder = new StringBuilder("""{"type":"FeatureCollection","features":[""");
        for (var i = 0; i < FeatureCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var x = (-122 + (i * 0.001)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var y = (37 + (i * 0.001)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                .Append(x).Append(',').Append(y)
                .Append("]},\"properties\":{\"n\":").Append(n).Append("}}");
        }

        builder.Append("]}");
        var payload = Encoding.UTF8.GetBytes(builder.ToString());

        var storage = _fixture.GetService<ICloudFileStorage>();
        await using var stream = new MemoryStream(payload);
        var upload = await storage.UploadAsync(new FileUploadRequest
        {
            Content = stream,
            FileName = $"{tableName}.geojson",
            ContentType = "application/geo+json",
            SizeBytes = payload.Length,
            TimeToLive = TimeSpan.FromHours(1),
            Folder = "imports",
        });
        upload.Success.Should().BeTrue(upload.ErrorMessage);

        return new ImportRequest
        {
            CloudFileId = upload.File!.FileId,
            FileName = $"{tableName}.geojson",
            SourceKind = "cloud-file",
            TableName = tableName,
            OverwriteExisting = true,
        };
    }

    private async Task WaitForTerminalAsync(string jobId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await _jobStore.GetAsync(jobId);
            if (job?.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled)
            {
                job.Status.Should().Be(ExecutionJobStatus.Succeeded, job.ErrorMessage);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"File-import job {jobId} did not finish within {timeout}.");
    }

    private async Task AssertCompletedAsync(DurableImportJobService service, string jobId, string tableName, string runner)
    {
        var progress = await service.GetProgressAsync(jobId);
        progress.Should().NotBeNull();
        progress!.Status.Should().Be(ImportStatus.Completed, progress.ErrorMessage);
        progress.Runner.Should().Be(runner);
        progress.InstanceId.Should().Be(InstanceId);
        progress.FeaturesProcessed.Should().Be(FeatureCount);

        await using var connection = new NpgsqlConnection(_fixture.DatabaseConnectionProvider.GetConnectionString());
        await connection.OpenAsync();
        await using var lookup = new NpgsqlCommand(
            "SELECT table_schema, table_name FROM information_schema.tables WHERE table_name = @name LIMIT 1",
            connection);
        lookup.Parameters.AddWithValue("name", ImportedPhysicalName(tableName));
        string schema;
        string physical;
        await using (var reader = await lookup.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue("the import created its target table");
            schema = reader.GetString(0);
            physical = reader.GetString(1);
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{physical}\"", connection);
        Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(FeatureCount);
    }

    private static string ImportedPhysicalName(string tableName)
        => Honua.Core.Features.FileImport.Services.ImportedSnapshotRefresher.ResolvePhysicalImportedTableNames(tableName)[^1];

    /// <summary>Stands in for AWS Batch: records the launch the worker process would receive.</summary>
    private sealed class LaunchRecordingBackend : IBatchComputeBackend
    {
        public List<string> Launched { get; } = [];

        public string BackendName => "test-batch";

        public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.AwsBatch;

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeBackendCapabilities { SupportsCancellation = true, SupportsProgressPolling = true });

        public Task<BatchComputeSubmissionResult> StartAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            Launched.Add(job.OperationId);
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = $"batch-{job.OperationId}",
            });
        }

        public Task<BatchComputeObservation> ObserveAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = job.Status, ProviderOperationId = job.ProviderOperationId });

        public Task<BatchComputeObservation> CancelAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled, ProviderOperationId = job.ProviderOperationId });
    }
}
