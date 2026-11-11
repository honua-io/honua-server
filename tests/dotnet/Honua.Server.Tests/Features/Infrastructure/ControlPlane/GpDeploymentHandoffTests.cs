// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Runtime handoff over the PostgreSQL job store and the real geometry executor. These are local
// substrate tests; the release journey must also run between two candidate images on ECS and
// Lambda/Batch.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class GpDeploymentHandoffTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EcsDrain_DeadlineExpires_FailsOnceWithoutReexecutingOrExposingPartialOutput(bool ignoresCancellation)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.JobStore;
        var queue = harness.Queue;
        var executor = new HeldCentroidExecutor { PublishPartialOutput = true, IgnoreCancellation = ignoresCancellation };
        var callback = new TerminalRecorder();
        using var worker = CreateWorker(queue, store, executor, callback);
        var job = CreateJob("rc.3", "rc.4");
        (await store.TryCreateAsync(job)).Should().BeTrue();
        await queue.EnqueueAsync(job.OperationId);
        using var deadline = new CancellationTokenSource();
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await store.GetAsync(job.OperationId))!.ArtifactReferences.Should().ContainSingle();
            var drain = worker.StopAsync(deadline.Token);
            deadline.Cancel();
            if (ignoresCancellation)
            {
                await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                drain.IsCompleted.Should().BeFalse("shutdown must retain the store until terminal cleanup completes");
                executor.Release.TrySetResult();
            }
            await drain;
            callback.Completed.Task.IsCompleted.Should().BeTrue("terminal cleanup must finish before StopAsync returns");
            var terminal = await callback.Completed.Task;
            terminal.Status.Should().Be(ExecutionJobStatus.Failed);
            terminal.ErrorMessage.Should().Be("Worker drain deadline expired.");
            terminal.ArtifactReferences.Should().BeEmpty();
            terminal.AttemptCount.Should().Be(1);
            terminal.CompletedAt.Should().NotBeNull();
            executor.Executions.Should().Be(1);
            callback.Count.Should().Be(1);
            (await queue.GetQueueDepthAsync()).Should().Be(0);
            await queue.EnqueueAsync(job.OperationId);
            (await queue.TryClaimAsync("replacement")).Should().BeNull();
        }
        finally
        {
            deadline.Cancel();
            executor.Release.TrySetResult();
            await worker.StopAsync(deadline.Token);
            if (worker.ExecuteTask is { } execution)
            {
                await execution.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [IntegrationTheory]
    [InlineData("rc.3", "rc.4", false)]
    [InlineData("rc.4", "rc.3", false)]
    [InlineData("rc.3", "rc.4", true)]
    [InlineData("rc.4", "rc.3", true)]
    public async Task BatchHandoff_AcceptedSubmissionLosesResponse_RecoversOnce(
        string sourceRevision, string targetRevision, bool hostStops)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.JobStore;
        var queue = harness.Queue;
        var client = Substitute.For<IAwsBatchJobClient>();
        using var shutdown = new CancellationTokenSource();
        const string providerId = "accepted-before-switch";
        AwsBatchJobSubmission? accepted = null;
        client.SubmitJobAsync(Arg.Any<AwsBatchJobSubmission>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                accepted = call.Arg<AwsBatchJobSubmission>();
                if (hostStops)
                {
                    shutdown.Cancel();
                }
                // The provider accepted the request; its response was lost to a transport
                // deadline or host shutdown. Neither outcome proves provider rejection.
                return Task.FromException<AwsBatchSubmitResult>(new TaskCanceledException("Submission response lost."));
            });
        client.ListJobsByNameAsync("queue", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<string>(1).Should().Be(accepted!.JobName);
                return Task.FromResult<IReadOnlyList<AwsBatchJobState>>(
                    [new AwsBatchJobState { JobId = providerId, Status = "RUNNABLE" }]);
            });
        var sourceBackend = new AwsBatchComputeBackend(client, NullLogger<AwsBatchComputeBackend>.Instance);
        var targetBackend = new AwsBatchComputeBackend(client, Options.Create(new AwsBatchExecutionOptions
        {
            JobDefinitions = [new AwsBatchJobDefinitionContractOptions { JobDefinition = targetRevision, MaxSupportedContractVersion = 1 }]
        }), NullLogger<AwsBatchComputeBackend>.Instance);
        var progress = Substitute.For<IUniversalProgressStore>();
        var source = CreateReconciler(store, sourceBackend, progress);
        var target = CreateReconciler(store, targetBackend, progress);
        var local = CreateJob(sourceRevision, targetRevision);
        var job = local with
        {
            Spec = local.Spec with
            {
                Backend = sourceBackend.BackendName,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Parameters = new Dictionary<string, string>(local.Spec.Parameters)
                {
                    [AwsBatchParameterKeys.JobDefinitionArn] = sourceRevision,
                    [AwsBatchParameterKeys.JobQueueArn] = "queue"
                }
            }
        };
        (await store.TryCreateAsync(job)).Should().BeTrue();
        await source.ReconcileExecutionJobAsync(job.OperationId, shutdown.Token);
        var interrupted = (await store.GetAsync(job.OperationId))!;
        interrupted.Status.Should().Be(hostStops ? ExecutionJobStatus.Provisioning : ExecutionJobStatus.Queued);
        interrupted.CompletedAt.Should().BeNull();
        accepted!.JobDefinition.Should().Be(sourceRevision);
        await target.ReconcileExecutionJobAsync(job.OperationId);
        var recovered = (await store.GetAsync(job.OperationId))!;
        recovered.ProviderOperationId.Should().Be(providerId);
        recovered.Status.Should().Be(ExecutionJobStatus.Queued);
        recovered.AttemptCount.Should().Be(hostStops ? 0 : 1);

        var executor = new HeldCentroidExecutor();
        var callback = new TerminalRecorder();
        using var worker = CreateWorker(CreateProviderLaunchedQueue(store, job.OperationId), store, executor, callback);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await executor.Started.Task.WaitAsync(deadline.Token);
            executor.Release.TrySetResult();
            var terminal = await callback.Completed.Task.WaitAsync(deadline.Token);
            AssertCentroid(terminal, expectedAttempts: recovered.AttemptCount);
            terminal.ProviderOperationId.Should().Be(providerId);
            terminal.Spec.Parameters[AwsBatchParameterKeys.JobDefinitionArn].Should().Be(sourceRevision);
            var version = (await store.GetAsync(job.OperationId))!.Version;
            await target.ReconcileExecutionJobAsync(job.OperationId);
            await source.ReconcileExecutionJobAsync(job.OperationId);
            (await store.GetAsync(job.OperationId))!.Version.Should().Be(version);
            await AssertRelaunchedProviderWorkerFindsTheJobTerminalAsync(store, job.OperationId);
            (await queue.TryClaimAsync("serving-host-worker")).Should().BeNull();
            (await queue.GetQueueDepthAsync()).Should().Be(0);
            executor.Executions.Should().Be(1);
            callback.Count.Should().Be(1);
            await client.Received(1).SubmitJobAsync(Arg.Any<AwsBatchJobSubmission>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await client.Received(1).ListJobsByNameAsync("queue", accepted.JobName, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await client.DidNotReceiveWithAnyArgs().CancelJobAsync(default!, default!, default);
            await client.DidNotReceiveWithAnyArgs().TerminateJobAsync(default!, default!, default);
        }
        finally
        {
            executor.Release.TrySetResult();
            await worker.StopAsync(deadline.Token);
        }
    }

    [IntegrationTheory]
    [InlineData("rc.3", "rc.4")]
    [InlineData("rc.4", "rc.3")]
    public async Task BatchHandoff_ReplacementControllerRetainsOriginalWorkerAndOutput(string sourceRevision, string targetRevision)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.JobStore;
        var queue = harness.Queue;
        var client = Substitute.For<IAwsBatchJobClient>();
        const string providerId = "original-batch-job";
        client.SubmitJobAsync(Arg.Any<AwsBatchJobSubmission>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new AwsBatchSubmitResult { JobId = providerId, JobName = call.Arg<AwsBatchJobSubmission>().JobName });
        client.DescribeJobAsync(providerId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AwsBatchJobState { JobId = providerId, Status = "RUNNING" });
        var sourceWorker = sourceRevision == "rc.3" ? "worker:1" : "worker:2";
        var targetWorker = targetRevision == "rc.3" ? "worker:1" : "worker:2";
        var sourceBackend = new AwsBatchComputeBackend(client, NullLogger<AwsBatchComputeBackend>.Instance);
        var targetBackend = new AwsBatchComputeBackend(client, Options.Create(new AwsBatchExecutionOptions
        {
            JobDefinitions = [new AwsBatchJobDefinitionContractOptions { JobDefinition = targetWorker, MaxSupportedContractVersion = 1 }]
        }), NullLogger<AwsBatchComputeBackend>.Instance);
        var progress = Substitute.For<IUniversalProgressStore>();
        var source = CreateReconciler(store, sourceBackend, progress);
        var target = CreateReconciler(store, targetBackend, progress);
        var local = CreateJob(sourceRevision, targetRevision);
        var parameters = new Dictionary<string, string>(local.Spec.Parameters)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = sourceWorker,
            [AwsBatchParameterKeys.JobQueueArn] = "queue"
        };
        var job = local with
        {
            Spec = local.Spec with { Backend = sourceBackend.BackendName, TargetKind = BatchComputeTargetKind.AwsBatch, Parameters = parameters }
        };
        (await store.TryCreateAsync(job)).Should().BeTrue();
        await source.ReconcileExecutionJobAsync(job.OperationId);
        (await store.GetAsync(job.OperationId))!.ProviderOperationId.Should().Be(providerId);
        (await store.GetAsync(job.OperationId))!.Status.Should().Be(ExecutionJobStatus.Queued);

        // The AWS transport is substituted; provider execution uses the real durable worker,
        // the claim a provider-launched worker makes and the geometry executor. Changing a serving
        // revision never stops this worker.
        var executor = new HeldCentroidExecutor();
        var callback = new TerminalRecorder();
        using var worker = CreateWorker(CreateProviderLaunchedQueue(store, job.OperationId), store, executor, callback);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await executor.Started.Task.WaitAsync(deadline.Token);
            using var shutdown = new CancellationTokenSource();
            client.DescribeJobAsync(providerId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    shutdown.Cancel();
                    return Task.FromCanceled<AwsBatchJobState?>(shutdown.Token);
                });
            await source.ReconcileExecutionJobAsync(job.OperationId, shutdown.Token);
            (await store.GetAsync(job.OperationId))!.Status.Should().Be(ExecutionJobStatus.Running);
            client.DescribeJobAsync(providerId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AwsBatchJobState { JobId = providerId, Status = "RUNNING" });
            await target.ReconcileExecutionJobAsync(job.OperationId);
            await client.Received(2).DescribeJobAsync(providerId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            executor.Release.TrySetResult();
            var terminal = await callback.Completed.Task.WaitAsync(deadline.Token);
            // The provider-launched worker runs the attempt its submission counted.
            AssertCentroid(terminal, expectedAttempts: 1);
            var version = (await store.GetAsync(job.OperationId))!.Version;
            await target.ReconcileExecutionJobAsync(job.OperationId);
            await source.ReconcileExecutionJobAsync(job.OperationId);
            (await store.GetAsync(job.OperationId))!.Version.Should().Be(version);
            (await queue.GetQueueDepthAsync()).Should().Be(0);
            executor.Executions.Should().Be(1);
            callback.Count.Should().Be(1);
            terminal.ProviderOperationId.Should().Be(providerId);
            terminal.Spec.Parameters[AwsBatchParameterKeys.JobDefinitionArn].Should().Be(sourceWorker);
            await client.Received(1).SubmitJobAsync(Arg.Any<AwsBatchJobSubmission>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await client.DidNotReceiveWithAnyArgs().CancelJobAsync(default!, default!, default);
            await client.DidNotReceiveWithAnyArgs().TerminateJobAsync(default!, default!, default);
        }
        finally
        {
            executor.Release.TrySetResult();
            await worker.StopAsync(deadline.Token);
        }
    }

    [IntegrationTheory]
    [InlineData("rc.3", "rc.4")]
    [InlineData("rc.4", "rc.3")]
    public async Task EcsDrain_WithRunningGeometryJob_CompletesOnce(string sourceRevision, string targetRevision)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.JobStore;
        var queue = harness.Queue;
        var executor = new HeldCentroidExecutor();
        var callback = new TerminalRecorder();
        using var source = CreateWorker(queue, store, executor, callback);
        using var target = CreateWorker(queue, store, executor, callback);
        var job = CreateJob(sourceRevision, targetRevision);
        (await store.TryCreateAsync(job)).Should().BeTrue();
        await queue.EnqueueAsync(job.OperationId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? drain = null;
        try
        {
            await source.StartAsync(CancellationToken.None);
            await executor.Started.Task.WaitAsync(deadline.Token);
            drain = source.StopAsync(deadline.Token);
            await target.StartAsync(CancellationToken.None);

            // StopAsync must be waiting for the original execution, with its ownership intact.
            drain.IsCompleted.Should().BeFalse();
            var running = (await store.GetAsync(job.OperationId))!;
            running.Status.Should().Be(ExecutionJobStatus.Running);
            running.AttemptCount.Should().Be(1);
            executor.Release.TrySetResult();
            await drain;
            var terminal = await callback.Completed.Task.WaitAsync(deadline.Token);
            AssertCentroid(terminal);
            executor.Executions.Should().Be(1);
            callback.Count.Should().Be(1);
            (await queue.GetQueueDepthAsync()).Should().Be(0);
            var storedTerminal = (await store.GetAsync(job.OperationId))!;

            // A replayed queue delivery after cutover cannot execute a terminal job again.
            await queue.EnqueueAsync(job.OperationId);
            (await queue.TryClaimAsync("replacement-probe", new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing }))
                .Should().BeNull();
            var reread = (await store.GetAsync(job.OperationId))!;
            reread.CompletedAt.Should().Be(storedTerminal.CompletedAt);
            reread.ArtifactReferences.Should().Equal(terminal.ArtifactReferences);
            executor.Executions.Should().Be(1);
        }
        finally
        {
            executor.Release.TrySetResult();
            await source.StopAsync(deadline.Token);
            await target.StopAsync(deadline.Token);
        }
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(GpDeploymentHandoffTests));

    private static JobExecutionService CreateWorker(IJobQueue queue, IExecutionJobStore store,
        IJobExecutor executor, IJobTerminalCallback callback)
        => new(queue, store, [executor], new ExecutionJobCancellationTokens(), [callback], null,
            NullLogger<JobExecutionService>.Instance);

    private static ExecutionJobReconciler CreateReconciler(
        PostgresExecutionJobStore store, IBatchComputeBackend backend, IUniversalProgressStore progress)
        => new(store, [backend], progress, NullLogger<ExecutionJobReconciler>.Instance, storeClock: store);

    // A job submitted to a compute provider never enters the local queue: the container the
    // provider starts claims the one operation it was launched for.
    private static AssignedExecutionJobQueue CreateProviderLaunchedQueue(PostgresExecutionJobStore store, string operationId)
        => new(
            new ExecutionWorkerLaunch { OperationId = operationId, JobKind = ExecutionJobKind.Geoprocessing },
            store,
            NullLogger<AssignedExecutionJobQueue>.Instance,
            storeClock: store);

    private static async Task AssertRelaunchedProviderWorkerFindsTheJobTerminalAsync(
        PostgresExecutionJobStore store, string operationId)
    {
        var relaunched = CreateProviderLaunchedQueue(store, operationId);
        (await relaunched.TryClaimAsync("duplicate-delivery")).Should().BeNull();
        (await relaunched.Completion).Should().Be(ExecutionWorkerClaimOutcome.AlreadyTerminal);
    }

    private static ExecutionJobRecord CreateJob(string sourceRevision, string targetRevision)
    {
        // Rectangle (2,4)-(10,16): centroid is independently (6,10); no geometry library
        // computes the expected result. This operation produces a 2D vector, so nodata is N/A.
        var polygon = new WKTReader().Read("POLYGON ((2 4,10 4,10 16,2 16,2 4))");
        var prefix = ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0.";
        return new ExecutionJobRecord
        {
            OperationId = $"handoff-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RetryPolicy = JobRetryPolicy.None,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"{sourceRevision}-to-{targetRevision}",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "geometry.centroid",
                    ["protocolProcessId"] = "geometry.centroid",
                    [prefix + "wkb"] = Convert.ToBase64String(new WKBWriter().Write(polygon)),
                    [prefix + "srid"] = "4326"
                }
            }
        };
    }

    private static void AssertCentroid(ExecutionJobRecord job, int expectedAttempts = 1)
    {
        job.Status.Should().Be(ExecutionJobStatus.Succeeded);
        job.AttemptCount.Should().Be(expectedAttempts);
        job.CompletedAt.Should().NotBeNull();
        var uri = job.ArtifactReferences.Should().ContainSingle().Which;
        const string prefix = "data:application/geo+json;base64,";
        uri.Should().StartWith(prefix);
        using var output = JsonDocument.Parse(Convert.FromBase64String(uri[prefix.Length..]));
        var feature = output.RootElement;
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
        feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
            .Select(value => value.GetDouble()).Should().Equal(6d, 10d);
        feature.GetProperty("properties").GetProperty("processId").GetString().Should().Be("geometry.centroid");
        feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
        feature.GetProperty("properties").GetProperty("inputGeometryType").GetString().Should().Be("Polygon");
    }

    private sealed class HeldCentroidExecutor : IJobExecutor
    {
        public bool PublishPartialOutput { get; init; }
        public bool IgnoreCancellation { get; init; }
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions;

        public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            using var cancellationRegistration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Interlocked.Increment(ref Executions);
            if (PublishPartialOutput)
            {
                await context.PublishArtifactAsync("data:application/json;base64,e30=", cancellationToken);
            }
            Started.TrySetResult();
            if (IgnoreCancellation)
            {
                await Release.Task;
                return JobExecutionResult.Succeeded();
            }
            await Release.Task.WaitAsync(cancellationToken);
            var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
            options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
            var executor = new GeometryCentroidJobExecutor(options, NullLogger<GeometryCentroidJobExecutor>.Instance);
            return await executor.ExecuteAsync(job, context, cancellationToken);
        }
    }

    private sealed class TerminalRecorder : IJobTerminalCallback
    {
        public TaskCompletionSource<ExecutionJobRecord> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            Completed.TrySetResult(job);
            return ValueTask.CompletedTask;
        }
    }
}
