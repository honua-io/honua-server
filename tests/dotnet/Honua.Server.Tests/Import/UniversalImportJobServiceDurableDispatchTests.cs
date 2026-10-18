// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
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
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Dispatch decision for background file imports (honua-server#5916): a configured durable
/// runner receives an execution job carrying the staged storage reference; a Lambda host without
/// one refuses; a long-lived host without one keeps the in-process runner.
/// </summary>
public sealed class UniversalImportJobServiceDurableDispatchTests
{
    private const string BatchBackendName = "honua-aws-batch";

    private static ExecutionJobDefinition GpBatchWorkload(string? runtimeProfile = null) => new()
    {
        WorkloadId = "gp-batch",
        TargetKind = BatchComputeTargetKind.AwsBatch,
        Backend = BatchBackendName,
        Kind = ExecutionJobKind.Geoprocessing,
        WorkloadName = "gp-batch",
        ArtifactReference = "honua/server:test",
        RuntimeProfile = runtimeProfile,
        Parameters = new Dictionary<string, string>
        {
            ["batch.job_queue_arn"] = "gp-queue",
            ["batch.job_definition_arn.s"] = "gp-def-s:3",
        },
    };

    private static ImportRequest StagedRequest() => new()
    {
        CloudFileId = "imports/2026/10/large.geojson",
        FileName = "large.geojson",
        SourceKind = "cloud-file",
        SourceUrl = "https://example.test/large.geojson",
        TableName = "large_import",
        TargetSchema = "honua_data",
        SourceSrid = 4326,
        TargetSrid = 3857,
        OverwriteExisting = true,
        CorrelationId = "corr-1",
        OperationInstanceId = "opinst-1",
    };

    [UnitTest]
    public void Decide_DurableRuntimeWithRemoteWorkload_RoutesToBatchEvenOnLambda()
    {
        var decision = BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: true);

        decision.Mode.Should().Be(BackgroundImportRunnerMode.DurableRemote);
        decision.Runner.Should().Be(ImportJobRunners.Batch);
        decision.AcceptsBackgroundImports.Should().BeTrue();
        decision.UnavailableReason.Should().BeNull();
    }

    [UnitTest]
    public void Decide_LambdaWithoutRunner_RefusesBackgroundImports()
    {
        foreach (var durable in new[] { false, true })
        {
            var decision = BackgroundImportRunnerDecision.Decide(durable, remoteWorkload: null, frozenBetweenRequests: true);

            decision.Mode.Should().Be(BackgroundImportRunnerMode.Unavailable);
            decision.Runner.Should().Be(ImportJobRunners.Unavailable);
            decision.AcceptsBackgroundImports.Should().BeFalse();
            decision.UnavailableReason.Should().Be(BackgroundImportRunnerDecision.DurableRunnerRequiredReason);
        }
    }

    [UnitTest]
    public void Decide_BatchRunnerWithHostLocalFileStorage_IsNotUsed()
    {
        var lambda = BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: true, sharedFileStorage: false);
        lambda.Mode.Should().Be(BackgroundImportRunnerMode.Unavailable);
        lambda.UnavailableReason.Should().Be(BackgroundImportRunnerDecision.SharedFileStorageRequiredReason);

        BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: false, sharedFileStorage: false).Mode
            .Should().Be(BackgroundImportRunnerMode.DurableLocal);
    }

    [UnitTest]
    public void Decide_LongLivedHost_UsesLocalDurableQueueOrInProcessRunner()
    {
        BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false).Mode
            .Should().Be(BackgroundImportRunnerMode.DurableLocal);
        BackgroundImportRunnerDecision.Decide(false, null, frozenBetweenRequests: false).Mode
            .Should().Be(BackgroundImportRunnerMode.InProcess);
        BackgroundImportRunnerDecision.Decide(false, GpBatchWorkload(), frozenBetweenRequests: false).Mode
            .Should().Be(BackgroundImportRunnerMode.InProcess, "a workload without the durable substrate cannot be drained");
        BackgroundImportRunnerDecision.Decide(true, null, false).Runner.Should().Be(ImportJobRunners.InProcess);
    }

    [UnitTest]
    public void IsFrozenBetweenRequestsHost_DetectsLambdaApiAndEventHandlers()
    {
        static IConfiguration Config(params (string Key, string Value)[] values)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(Config(("AWS_LAMBDA_FUNCTION_NAME", "honua-api")))
            .Should().BeTrue();
        BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(Config(("HONUA_CONTROL_PLANE_LAMBDA_HANDLER", "scheduled-tick")))
            .Should().BeTrue();
        BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(Config(("HOSTNAME", "ecs-task")))
            .Should().BeFalse();
    }

    [UnitTest]
    public void SelectRemoteWorkload_PrefersFileImportWorkloadAndSkipsLocalCustomCodeAndUnregistered()
    {
        var backends = new IBatchComputeBackend[] { new StubBatchComputeBackend(BatchBackendName, BatchComputeTargetKind.AwsBatch) };
        var local = GpBatchWorkload() with { WorkloadId = "local", Backend = LocalBatchComputeBackend.BackendId, TargetKind = BatchComputeTargetKind.KubernetesJob };
        var customCode = GpBatchWorkload("custom-code") with { WorkloadId = "custom" };
        var unregistered = GpBatchWorkload() with { WorkloadId = "azure", Backend = "honua-azure-batch", TargetKind = BatchComputeTargetKind.AzureBatch };
        var gp = GpBatchWorkload();
        var import = GpBatchWorkload() with { WorkloadId = "import-batch", Kind = ExecutionJobKind.FileImport };

        BackgroundImportRunnerDecision.SelectRemoteWorkload([local, customCode, unregistered], backends).Should().BeNull();
        BackgroundImportRunnerDecision.SelectRemoteWorkload([local, gp], backends)!.WorkloadId.Should().Be("gp-batch");
        BackgroundImportRunnerDecision.SelectRemoteWorkload([gp, import], backends)!.WorkloadId.Should().Be("import-batch");
    }

    [UnitTest]
    public async Task QueueImportAsync_WithBatchRunner_SubmitsFileImportJobCarryingStorageReference()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var backend = new StubBatchComputeBackend(BatchBackendName, BatchComputeTargetKind.AwsBatch);
        var inProcess = new ProgressReadingImportJobService(progressStore);
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: true),
            inProcess, jobStore, progressStore, [backend]);

        var jobId = await service.QueueImportAsync(StagedRequest(), 85_688_964);

        inProcess.QueuedRequests.Should().BeEmpty("a durable runner never runs the import in the accepting host");
        backend.StartCalls.Should().Be(1);
        var job = await jobStore.GetAsync(jobId);
        job.Should().NotBeNull();
        job!.Spec.Kind.Should().Be(ExecutionJobKind.FileImport);
        job.Spec.Backend.Should().Be(BatchBackendName);
        job.Spec.TargetKind.Should().Be(BatchComputeTargetKind.AwsBatch);
        job.Spec.WorkloadId.Should().Be("gp-batch");
        job.Spec.Artifact.Should().Be("honua/server:test");
        job.AttemptCount.Should().Be(1);
        job.ProviderOperationId.Should().Be("provider-1");
        job.Audit.CorrelationId.Should().Be("corr-1");
        job.Spec.Parameters.Should().Contain(new Dictionary<string, string>
        {
            ["batch.job_queue_arn"] = "gp-queue",
            ["batch.job_definition_arn.s"] = "gp-def-s:3",
            [FileImportJobParameters.CloudFileId] = "imports/2026/10/large.geojson",
            [FileImportJobParameters.TableName] = "large_import",
            [FileImportJobParameters.TargetSchema] = "honua_data",
            [FileImportJobParameters.SourceSrid] = "4326",
            [FileImportJobParameters.TargetSrid] = "3857",
            [FileImportJobParameters.OverwriteExisting] = "true",
            [FileImportJobParameters.SourceUrl] = "https://example.test/large.geojson",
            [FileImportJobParameters.FileSizeBytes] = "85688964",
            [FileImportJobParameters.Runner] = ImportJobRunners.Batch,
        });

        var progress = await service.GetProgressAsync(jobId);
        progress!.Status.Should().Be(ImportStatus.Queued);
        progress.Runner.Should().Be(ImportJobRunners.Batch);
        progress.InstanceId.Should().Be("api-instance-1");
        progress.CloudFileId.Should().Be("imports/2026/10/large.geojson");
    }

    [UnitTest]
    public async Task QueueImportAsync_WithLocalDurableRunner_EnqueuesOnTheSharedQueue()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var queue = Substitute.For<IJobQueue>();
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], queue);

        var jobId = await service.QueueImportAsync(StagedRequest(), 1024);

        await queue.Received(1).EnqueueAsync(jobId, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        var job = await jobStore.GetAsync(jobId);
        job!.Spec.Backend.Should().Be(LocalBatchComputeBackend.BackendId);
        job.Spec.Kind.Should().Be(ExecutionJobKind.FileImport);
        job.Spec.Parameters[FileImportJobParameters.Runner].Should().Be(ImportJobRunners.InProcess);
        (await service.GetProgressAsync(jobId))!.Runner.Should().Be(ImportJobRunners.InProcess);
    }

    [UnitTest]
    public async Task QueueImportAsync_WithoutDurableRunnerOnLongLivedHost_UsesInProcessRunner()
    {
        var progressStore = new InMemoryProgressStore();
        var inProcess = new ProgressReadingImportJobService(progressStore);
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(false, null, frozenBetweenRequests: false),
            inProcess, jobStore: null, progressStore, []);

        var jobId = await service.QueueImportAsync(StagedRequest(), 1024);

        jobId.Should().Be("in-process-job");
        inProcess.QueuedRequests.Should().ContainSingle();
    }

    [UnitTest]
    public async Task QueueImportAsync_OnLambdaWithoutRunner_Refuses()
    {
        var progressStore = new InMemoryProgressStore();
        var inProcess = new ProgressReadingImportJobService(progressStore);
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(false, null, frozenBetweenRequests: true),
            inProcess, jobStore: null, progressStore, []);

        var act = () => service.QueueImportAsync(StagedRequest(), 1024);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Background import service not available*");
        inProcess.QueuedRequests.Should().BeEmpty();
    }

    [UnitTest]
    public async Task QueueImportAsync_WhenDurableImportQueueIsFull_ThrowsQueueFull()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var queue = Substitute.For<IJobQueue>();
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], queue,
            new ImportLimits { MaxConcurrentBackgroundJobs = 1, MaxBackgroundJobQueueDepth = 1 });

        await service.QueueImportAsync(StagedRequest(), 1);
        await service.QueueImportAsync(StagedRequest(), 1);
        var act = () => service.QueueImportAsync(StagedRequest(), 1);

        await act.Should().ThrowAsync<ImportJobQueueFullException>();
        await queue.Received(2).EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GetActiveJobsAsync_DropsDurableJobsWhoseExecutionRecordEnded()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], Substitute.For<IJobQueue>());
        var live = await service.QueueImportAsync(StagedRequest(), 1);
        var dead = await service.QueueImportAsync(StagedRequest(), 1);
        await jobStore.SetAsync((await jobStore.GetAsync(dead))! with { Status = ExecutionJobStatus.Failed });

        var active = await service.GetActiveJobsAsync();

        active.Select(p => p.JobId).Should().Equal(live);
    }

    [UnitTest]
    public async Task GetProgressAsync_WhenExecutionRecordFailedWithoutTerminalDocument_ReportsFailed()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var backend = new StubBatchComputeBackend(BatchBackendName, BatchComputeTargetKind.AwsBatch);
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: true),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [backend]);
        var jobId = await service.QueueImportAsync(StagedRequest(), 2048);

        // The worker died mid-import: the document says processing, the reconciler failed the record.
        var processing = (await progressStore.GetProgressAsync<ImportProgress>(jobId))! with { Status = ImportStatus.Processing };
        await progressStore.SetProgressAsync(jobId, processing);
        var record = (await jobStore.GetAsync(jobId))!;
        await jobStore.SetAsync(record with
        {
            Status = ExecutionJobStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Failed (heartbeat expired)",
        });

        var progress = await service.GetProgressAsync(jobId);

        progress!.Status.Should().Be(ImportStatus.Failed);
        progress.CompletedAt.Should().NotBeNull();
        progress.Runner.Should().Be(ImportJobRunners.Batch);
    }

    [UnitTest]
    public async Task CancelJobAsync_UnclaimedLocalJob_CancelsRecordAndDocument()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], Substitute.For<IJobQueue>());
        var jobId = await service.QueueImportAsync(StagedRequest(), 2048);

        (await service.CancelJobAsync(jobId)).Should().BeTrue();

        (await jobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        (await service.GetProgressAsync(jobId))!.Status.Should().Be(ImportStatus.Cancelled);
        (await service.CancelJobAsync(jobId)).Should().BeFalse("a terminal job cannot be cancelled again");
    }

    [UnitTheory]
    [InlineData(ExecutionJobStatus.Queued, ImportStatus.Queued)]
    [InlineData(ExecutionJobStatus.Provisioning, ImportStatus.Queued)]
    [InlineData(ExecutionJobStatus.Running, ImportStatus.Processing)]
    [InlineData(ExecutionJobStatus.Succeeded, ImportStatus.Completed)]
    [InlineData(ExecutionJobStatus.Failed, ImportStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled, ImportStatus.Cancelled)]
    public async Task GetProgressAsync_MissingProjection_RecoversOriginalJobWithoutDispatch(
        ExecutionJobStatus executionStatus, ImportStatus expectedStatus)
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var queue = Substitute.For<IJobQueue>();
        var decision = BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false);
        var request = StagedRequest() with
        {
            Ownership = new ImportOperationOwnership("stored-key:original", "original-tenant"),
            UploadId = "original-upload",
        };
        var acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var record = DurableImportJobService.BuildJobRecord("accepted-job", request, 2048, decision, acceptedAt) with
        {
            Status = executionStatus,
            UpdatedAt = acceptedAt.AddMinutes(5),
            CompletedAt = executionStatus is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled
                ? acceptedAt.AddMinutes(5) : null,
        };
        await jobStore.TryCreateAsync(record);
        using var service = CreateService(decision,
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], queue);

        var recovered = await service.GetProgressAsync(record.OperationId);

        recovered.Should().NotBeNull();
        recovered!.Status.Should().Be(expectedStatus);
        recovered.JobId.Should().Be(record.OperationId);
        recovered.Ownership.Should().Be(request.Ownership);
        recovered.UploadId.Should().Be(request.UploadId);
        recovered.OperationInstanceId.Should().Be(request.OperationInstanceId);
        recovered.StartedAt.Should().Be(acceptedAt);
        recovered.CompletedAt.Should().Be(record.CompletedAt);
        recovered.FileName.Should().Be(request.FileName);
        recovered.PhysicalTableName.Should().BeNull("the requested name is not a committed output receipt");
        recovered.Schema.Should().BeNull();
        recovered.InstanceId.Should().BeNull("the observer is not the original accepting worker");
        await queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default);
        (await progressStore.GetProgressAsync<ImportProgress>(record.OperationId)).Should().BeNull(
            "a read must not overwrite a worker's concurrent progress projection");
    }

    [UnitTest]
    public async Task GetProgressAsync_MissingProjection_RejectsOtherJobKindsAndMalformedImportSpecs()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var decision = BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false);
        var valid = DurableImportJobService.BuildJobRecord("other-kind", StagedRequest(), 10, decision, DateTimeOffset.UtcNow);
        await jobStore.TryCreateAsync(valid with { Spec = valid.Spec with { Kind = ExecutionJobKind.Geoprocessing } });
        await jobStore.TryCreateAsync(valid with
        {
            OperationId = "malformed",
            Spec = valid.Spec with { Parameters = new Dictionary<string, string>() },
        });
        using var service = CreateService(decision,
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, []);

        (await service.GetProgressAsync("other-kind")).Should().BeNull();
        (await service.GetProgressAsync("malformed")).Should().BeNull();
        (await service.GetProgressAsync("missing")).Should().BeNull();
    }

    [UnitTest]
    public async Task GetProgressAsync_ExpiredSuccessfulProjection_DoesNotInventOutputOrRestartJob()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var backend = new StubBatchComputeBackend(BatchBackendName, BatchComputeTargetKind.AwsBatch);
        using var service = CreateService(
            BackgroundImportRunnerDecision.Decide(true, GpBatchWorkload(), frozenBetweenRequests: true),
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [backend]);
        var jobId = await service.QueueImportAsync(StagedRequest(), 2048);
        var record = (await jobStore.GetAsync(jobId))!;
        await jobStore.SetAsync(record with { Status = ExecutionJobStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow });
        await progressStore.DeleteProgressAsync(jobId);

        var recovered = await service.GetProgressAsync(jobId);

        recovered!.Status.Should().Be(ImportStatus.Completed);
        recovered.PhysicalTableName.Should().BeNull();
        recovered.Schema.Should().BeNull();
        recovered.CurrentPhase.Should().Contain("output details unavailable");
        backend.StartCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task GetProgressAsync_ExpiredProjection_RecoversActualOutputFromDurableReceipt()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new InMemoryProgressStore();
        var queue = Substitute.For<IJobQueue>();
        var decision = BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false);
        var request = StagedRequest() with { Ownership = new ImportOperationOwnership("original-actor", "tenant-a") };
        var record = DurableImportJobService.BuildJobRecord("completed-import", request, 2048, decision, DateTimeOffset.UtcNow) with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            FileImportOutput = new FileImportOutputReceipt
            {
                PhysicalTableName = "provider_actual_42", Schema = "tenant_data", FeatureCount = 17, DetectedSrid = 4326,
            },
        };
        await jobStore.TryCreateAsync(record);
        using var service = CreateService(decision,
            new ProgressReadingImportJobService(progressStore), jobStore, progressStore, [], queue);

        var recovered = await service.GetProgressAsync(record.OperationId);

        recovered!.Status.Should().Be(ImportStatus.Completed);
        recovered.PhysicalTableName.Should().Be("provider_actual_42");
        recovered.Schema.Should().Be("tenant_data");
        recovered.FeaturesProcessed.Should().Be(17);
        recovered.DetectedSrid.Should().Be(4326);
        recovered.Ownership.Should().Be(request.Ownership);
        recovered.TableName.Should().Be(request.TableName);
        recovered.CurrentPhase.Should().Be("Import completed");
        await queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, default, default);
        (await progressStore.GetProgressAsync<ImportProgress>(record.OperationId)).Should().BeNull();
    }

    [UnitTest]
    public void FileImportJobParameters_RoundTripTheStagedRequest()
    {
        var request = StagedRequest();
        var parameters = FileImportJobParameters.Build(request, 42);

        FileImportJobParameters.TryParse(parameters, out var parsed, out var size, out var error).Should().BeTrue(error);

        size.Should().Be(42);
        parsed.Should().BeEquivalentTo(request);
    }

    [UnitTest]
    public void FileImportJobParameters_RefuseALocalOnlySource()
    {
        var act = () => FileImportJobParameters.Build(
            new ImportRequest { LocalFilePath = "/tmp/x.geojson", FileName = "x.geojson", TableName = "x" }, 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*shared file storage*");
    }

    private static DurableImportJobService CreateService(
        BackgroundImportRunnerDecision decision,
        IImportJobService inProcess,
        IExecutionJobStore? jobStore,
        IUniversalProgressStore progressStore,
        IEnumerable<IBatchComputeBackend> backends,
        IJobQueue? queue = null,
        ImportLimits? limits = null)
    {
        var importService = Substitute.For<IFileImportService>();
        importService.DetectFormat(Arg.Any<string>()).Returns(SupportedFileFormat.GeoJson);
        importService.Limits.Returns(limits ?? new ImportLimits());
        var services = new ServiceCollection();
        services.AddScoped(_ => importService);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var identity = Substitute.For<IInstanceIdentity>();
        identity.InstanceId.Returns("api-instance-1");

        return new DurableImportJobService(
            inProcess,
            decision,
            progressStore,
            scopeFactory,
            backends,
            NullLogger<DurableImportJobService>.Instance,
            jobStore,
            queue,
            identity);
    }

    // ----- Test doubles -----------------------------------------------------

    private sealed class ProgressReadingImportJobService(IUniversalProgressStore progressStore) : IImportJobService
    {
        public List<ImportRequest> QueuedRequests { get; } = [];

        public Task<string> QueueImportAsync(ImportRequest request, long fileSize, CancellationToken cancellationToken = default)
        {
            QueuedRequests.Add(request);
            return Task.FromResult("in-process-job");
        }

        public Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
            => progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken);

        public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
            => progressStore.GetActiveOperationsAsync<ImportProgress>(OperationType.Import, cancellationToken);
    }

    private sealed class StubBatchComputeBackend(string name, BatchComputeTargetKind targetKind) : IBatchComputeBackend
    {
        public int StartCalls { get; private set; }

        public string BackendName => name;

        public BatchComputeTargetKind TargetKind => targetKind;

        public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeBackendCapabilities { SupportsCancellation = true, SupportsProgressPolling = true });

        public Task<BatchComputeSubmissionResult> StartAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = "provider-1",
            });
        }

        public Task<BatchComputeObservation> ObserveAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = job.Status, ProviderOperationId = job.ProviderOperationId });

        public Task<BatchComputeObservation> CancelAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled, ProviderOperationId = job.ProviderOperationId });
    }

    private sealed class InMemoryExecutionJobStore : IExecutionJobStore
    {
        private readonly ConcurrentDictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryAdd(job.OperationId, job));

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryGetValue(operationId, out var record) ? record : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = _records.Values.ToArray() });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_records.Values.ToArray());

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.FromResult(ProgressCompareAndSetResult.Updated);
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _progress.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values.OfType<TProgress>().ToArray());
    }
}
