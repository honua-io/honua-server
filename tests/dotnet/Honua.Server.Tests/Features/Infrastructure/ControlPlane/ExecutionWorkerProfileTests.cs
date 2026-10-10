// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Licensing;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using HttpServer = Microsoft.AspNetCore.Hosting.Server.IServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// e2e-cloud-aws run 38048901509 (aws-serverless/redis-on): the GP Batch worker runs the generic
/// server image. With Redis connected it booted as a full server, and its execution-job reconciler
/// re-dispatched the shared queued GP jobs to AWS Batch from inside the worker (EventId 9046,
/// <c>batch:SubmitJob</c> denied for the gp-job role), failing <c>geometry.buffer</c> and leaving
/// <c>geometry.area</c> running. These tests pin the worker-only composition profile that replaces it.
/// </summary>
[Collection("ControlPlaneTransitionTelemetry")]
public sealed class ExecutionWorkerProfileTests
{
    private const string OperationId = "gp-512eacce12104f1aa4f4a092c4c17408";

    // ---------------------------------------------------------------------
    // Worker-mode detection and the launch contract
    // ---------------------------------------------------------------------

    [UnitTest]
    public void Resolve_WithoutOperationId_IsTheServer()
    {
        var selection = HostCompositionSelection.Resolve(Configuration(("HONUA_JOB_KIND", "Geoprocessing")));

        selection.Profile.Should().Be(HostCompositionProfile.Server);
        selection.IsExecutionWorker.Should().BeFalse();
        selection.Launch.Should().BeNull();
    }

    [UnitTest]
    public void Resolve_WithBlankOperationId_IsTheServer()
        => HostCompositionSelection.Resolve(Configuration((ExecutionWorkerLaunch.OperationIdVariable, "  ")))
            .IsExecutionWorker.Should().BeFalse();

    [UnitTest]
    public void Resolve_TheLaunchEnvironmentEveryBackendStamps_SelectsTheExecutionWorkerProfile()
    {
        var job = SubmittedRemoteJob() with
        {
            AttemptCount = 0,
            Spec = SubmittedRemoteJob().Spec with { WorkloadId = "geoprocessing-aws-batch" },
        };

        // Backends stamp the record they receive before the submission is counted: attempt 0 + 1.
        var launchEnvironment = ExecutionWorkerLaunchEnvironment.Build(job, job.AttemptCount + 1);
        var selection = HostCompositionSelection.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(launchEnvironment.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build());

        selection.Profile.Should().Be(HostCompositionProfile.ExecutionWorker);
        selection.Launch.Should().BeEquivalentTo(new ExecutionWorkerLaunch
        {
            OperationId = OperationId,
            JobKind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "Geoprocessing (AWS Batch)",
            WorkloadId = "geoprocessing-aws-batch",
            ContractVersion = 1,
            ExecutionAttempt = 1,
        });
    }

    [UnitTest]
    public void LaunchEnvironment_ReservesEveryLaunchVariableFromWorkloadPassthrough()
    {
        ExecutionWorkerLaunchEnvironment.ReservedNames.Should().BeEquivalentTo(
        [
            "HONUA_OPERATION_ID", "HONUA_JOB_KIND", "HONUA_WORKLOAD_NAME", "HONUA_WORKLOAD_ID",
            "HONUA_RUNTIME_PROFILE", "HONUA_CONTRACT_VERSION", "HONUA_EXECUTION_ATTEMPT",
        ]);
        ExecutionWorkerLaunchEnvironment.IsReserved("GDAL_CACHEMAX").Should().BeFalse();
    }

    [UnitTest]
    public void Resolve_WithoutContractVersion_DefaultsToVersionOne()
        => HostCompositionSelection.Resolve(Configuration((ExecutionWorkerLaunch.OperationIdVariable, OperationId)))
            .Launch!.ContractVersion.Should().Be(1);

    [Theory]
    [InlineData("HONUA_JOB_KIND", "NotAKind")]
    [InlineData("HONUA_JOB_KIND", "0")]
    [InlineData("HONUA_CONTRACT_VERSION", "two")]
    [InlineData("HONUA_CONTRACT_VERSION", "0")]
    [InlineData("HONUA_CONTRACT_VERSION", "-1")]
    [InlineData("HONUA_EXECUTION_ATTEMPT", "0")]
    [InlineData("HONUA_EXECUTION_ATTEMPT", "first")]
    [Trait("Tier", "Fast")]
    public void Resolve_UnparseableLaunch_RefusesToStart(string name, string value)
    {
        var resolve = () => HostCompositionSelection.Resolve(Configuration(
            (ExecutionWorkerLaunch.OperationIdVariable, OperationId), (name, value)));

        resolve.Should().Throw<InvalidOperationException>().WithMessage($"*{OperationId}*");
    }

    // ---------------------------------------------------------------------
    // Host composition
    // ---------------------------------------------------------------------

    [UnitTest]
    public void WorkerProfile_WithRedisConnected_ComposesNoDispatcherReconcilerGatewayOrHttpSurface()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        var services = builder.Services;
        ComposeRedisConnectedControlPlane(services, builder.Configuration, builder.Environment);

        // The serving composition the worker used to inherit: proves the assertions below bite.
        HostedImplementations(services).Should().Contain(
        [
            typeof(ExecutionJobReconcilerBackgroundService),
            typeof(DeployWorkflowReconcilerBackgroundService),
            typeof(ExecutionJobBackstopSweepService),
            typeof(JobExecutionService),
        ]);
        services.Should().Contain(static descriptor => descriptor.ServiceType == typeof(HttpServer)
            && descriptor.ImplementationType != typeof(NoHttpServer), "the serving host binds Kestrel");
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBatchComputeBackend));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IOperationGateway));

        services.ApplyExecutionWorkerProfile(Launch());

        HostedImplementations(services).Should().Equal(
            [typeof(ExecutionWorkerLifetimeService)],
            "nothing composed for the serving host is hosted by the worker");
        services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType is null)
            .Should().HaveCount(2, "only the worker execution loop and license revalidation are re-added");
        services.Should().Contain(static descriptor => descriptor.ServiceType == typeof(FileBackedLicenseService));
        services.Where(static descriptor => descriptor.ServiceType == typeof(HttpServer))
            .Should().ContainSingle().Which.ImplementationType.Should().Be<NoHttpServer>(
                "the worker binds no port, so no HTTP surface is served");

        foreach (var removed in new[]
        {
            typeof(IBatchComputeBackend),
            typeof(IDeployBackend),
            typeof(IExecutionJobReconciler),
            typeof(IOperationReconcileDispatcher),
            typeof(IScheduledTickDispatcher),
            typeof(IScheduledTickHandler),
            typeof(IOperationGateway),
            typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor),
            typeof(RedisJobQueue),
            typeof(JobReconciliationService),
        })
        {
            services.Should().NotContain(descriptor => descriptor.ServiceType == removed,
                $"the execution worker profile never composes {removed.Name}");
        }

        // Build the real WebApplication: Build() adds the generic web host service, which must
        // find the no-op server, and must not re-register Kestrel.
        using var app = builder.Build();
        var provider = app.Services;
        provider.GetRequiredService<HttpServer>().Should().BeOfType<NoHttpServer>();
        provider.GetRequiredService<IJobQueue>().Should().BeOfType<AssignedExecutionJobQueue>(
            "the execution loop can claim only the operation the provider launched this process for");
        provider.GetServices<IBatchComputeBackend>().Should().BeEmpty("a worker has no submission path");
        provider.GetService<IOperationGateway>().Should().BeNull();
    }

    [UnitTest]
    public void WorkerProfile_WithoutTheDurableJobStore_RefusesToStart()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var apply = () => services.ApplyExecutionWorkerProfile(Launch());

        apply.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationId}*ConnectionStrings:redis*");
    }

    // ---------------------------------------------------------------------
    // Assigned operation claim
    // ---------------------------------------------------------------------

    [UnitTest]
    public async Task AssignedQueue_ClaimsOnlyTheLaunchedAttempt_WithoutCountingAnother()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob(), SubmittedRemoteJob("gp-someone-else"));
        var queue = Queue(store);

        var claimed = await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted);

        claimed.Should().Be(OperationId);
        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Provisioning);
        record.ClaimedBy.Should().Be("worker-a");
        record.AttemptCount.Should().Be(1, "the provider submission that launched the worker already counted the attempt");
        record.ProviderOperationId.Should().Be("aws-batch-job-1");
        store.Get("gp-someone-else").ClaimedBy.Should().BeNull("a worker never claims another job");

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();
        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.Executed);
    }

    [UnitTest]
    public async Task AssignedQueue_LaunchForAnOlderAttempt_IsStale()
    {
        // A stale container, or one the provider retried natively, must never run a newer attempt.
        var store = new VersionedJobStore(SubmittedRemoteJob() with { AttemptCount = 2, ProviderOperationId = "aws-batch-job-9" });
        var queue = Queue(store);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.StaleLaunch);
        store.Get(OperationId).ClaimedBy.Should().BeNull();
    }

    [UnitTest]
    public async Task AssignedQueue_LaunchAheadOfTheRecordedSubmission_WaitsInsteadOfRacingIt()
    {
        // The provider started the container before the submitting server's post-start write.
        var inFlight = SubmittedRemoteJob() with
        {
            Status = ExecutionJobStatus.Provisioning,
            AttemptCount = 0,
            ProviderOperationId = null,
        };
        var store = new VersionedJobStore(inFlight);
        var queue = Queue(store);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        queue.Completion.IsCompleted.Should().BeFalse("the worker polls again until the submission is recorded");
        store.Get(OperationId).Should().Be(inFlight, "the record is untouched");

        store.Replace(inFlight with { Status = ExecutionJobStatus.Queued, AttemptCount = 1, ProviderOperationId = "aws-batch-job-1" });
        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().Be(OperationId);
    }

    [UnitTest]
    public async Task AssignedQueue_RecordQueuedForResubmission_IsStale()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob() with
        {
            ProviderOperationId = null,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        var queue = Queue(store);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.StaleLaunch);
        store.Get(OperationId).ClaimedBy.Should().BeNull();
    }

    [UnitTest]
    public async Task AssignedQueue_LiveClaimHeldByAnotherWorker_IsRefused()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob() with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-other",
            ClaimedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
        });
        var queue = Queue(store);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.OwnedByAnotherWorker);
        store.Get(OperationId).ClaimedBy.Should().Be("worker-other");
    }

    [UnitTest]
    public async Task AssignedQueue_TerminalJob_HasNothingToRun()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob() with { Status = ExecutionJobStatus.Succeeded });
        var queue = Queue(store);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.AlreadyTerminal);
    }

    [UnitTest]
    public async Task AssignedQueue_UnsupportedContractVersion_FailsTheJobClosed_AndPublishesTheTerminalTransition()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob() with
        {
            Spec = SubmittedRemoteJob().Spec with { ContractVersion = ExecutionWorkerLaunch.MaxSupportedContractVersion + 1 },
        });
        var callback = Substitute.For<IJobTerminalCallback>();
        var services = new ServiceCollection().AddSingleton(callback).BuildServiceProvider();
        var queue = new AssignedExecutionJobQueue(Launch(), store, NullLogger<AssignedExecutionJobQueue>.Instance, services);

        (await queue.TryClaimAsync("worker-a", Kinds(), RuntimeProfiles.DefaultAccepted)).Should().BeNull();

        (await queue.Completion).Should().Be(ExecutionWorkerClaimOutcome.Unrunnable);
        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Failed);
        record.ErrorMessage.Should().Contain("contract version");
        await callback.Received(1).OnTerminalAsync(
            Arg.Is<ExecutionJobRecord>(job => job.Status == ExecutionJobStatus.Failed), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task AssignedQueue_NeverDispatchesWork()
    {
        var queue = Queue(new VersionedJobStore(SubmittedRemoteJob()));

        var enqueue = () => queue.EnqueueAsync("gp-other");

        await enqueue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot enqueue*");
    }

    // ---------------------------------------------------------------------
    // End to end: the shared execution loop runs the assigned job and reports it
    // ---------------------------------------------------------------------

    [UnitTest]
    public async Task Worker_RunsTheAssignedJob_AndReportsSuccessThroughTheDurableStore()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob(), SubmittedRemoteJob("gp-queued-local"));
        var executor = new ScriptedExecutor(async (job, context, cancellationToken) =>
        {
            await context.PublishArtifactAsync("gp-result://area", cancellationToken);
            return JobExecutionResult.Succeeded();
        });

        var (exitCode, stopped) = await RunWorkerAsync(store, executor);

        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Succeeded);
        record.PercentComplete.Should().Be(100);
        record.ArtifactReferences.Should().Equal("gp-result://area");
        record.AttemptCount.Should().Be(1);
        executor.ExecutedOperations.Should().Equal(OperationId);
        store.Get("gp-queued-local").Status.Should().Be(ExecutionJobStatus.Queued, "the worker touched no other job");
        exitCode.Should().Be(0);
        stopped.Should().BeTrue();
    }

    [UnitTest]
    public async Task Worker_ConcurrentProviderObservationAtFinalization_StillRecordsTheSuccess()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob());
        var executor = new ScriptedExecutor((job, _, _) =>
        {
            // The serving host's reconciler writes a nonterminal provider observation right before
            // the worker's terminal CAS.
            store.InterleaveBeforeNextTerminalWrite(current => current with { CurrentPhase = "AWS Batch job status=RUNNING" });
            return Task.FromResult(JobExecutionResult.Succeeded());
        });

        var (exitCode, _) = await RunWorkerAsync(store, executor);

        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Succeeded, "a CAS conflict while still owning the attempt is retried");
        record.ProviderOperationId.Should().Be("aws-batch-job-1", "the executed attempt is not handed back for another run");
        executor.ExecutedOperations.Should().ContainSingle();
        exitCode.Should().Be(0);
    }

    [UnitTest]
    public async Task Worker_DeterministicExecutorFailure_IsReportedAsTheTypedTerminalError()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob());
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult(
            JobExecutionResult.Failed("geometry.buffer: distance must be finite.") with { IsRetryable = false }));

        var (exitCode, _) = await RunWorkerAsync(store, executor);

        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Failed);
        record.ErrorMessage.Should().Be("geometry.buffer: distance must be finite.");
        exitCode.Should().Be(0, "the outcome is durable; a provider-native retry must not run the job again");
    }

    [UnitTest]
    public async Task Worker_RetryableFailure_HandsTheAttemptBackToTheProviderReconciler()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob());
        var executor = new ScriptedExecutor((_, _, _) => Task.FromResult(JobExecutionResult.Failed("source timed out")));

        var (exitCode, _) = await RunWorkerAsync(store, executor);

        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Queued);
        record.ClaimedBy.Should().BeNull();
        record.ProviderOperationId.Should().BeNull();
        record.NextRetryAt.Should().NotBeNull(
            "with a retry time and no provider marker the serving host's reconciler resubmits it (and counts the attempt)");
        exitCode.Should().Be(0, "the reconciler owns the retry, so the provider must not retry natively");
    }

    [UnitTest]
    public async Task Worker_ProviderInterruption_HandsTheAttemptBackInsteadOfFailingIt()
    {
        var store = new VersionedJobStore(SubmittedRemoteJob());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ScriptedExecutor(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JobExecutionResult.Succeeded();
        });
        var launch = Launch();
        var queue = new AssignedExecutionJobQueue(launch, store, NullLogger<AssignedExecutionJobQueue>.Instance);
        using var loop = new JobExecutionService(
            queue, store, [executor], new ExecutionJobCancellationTokens(), [], null,
            NullLogger<JobExecutionService>.Instance)
        {
            RequeueOnShutdown = true,
        };

        await loop.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // SIGTERM from the provider: the host gives the loop a short stop budget.
        using var hostDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await loop.StopAsync(hostDeadline.Token);

        var record = store.Get(OperationId);
        record.Status.Should().Be(ExecutionJobStatus.Queued, "an interruption is not a drain-deadline failure");
        record.ErrorMessage.Should().BeNull();
        record.ProviderOperationId.Should().BeNull();
        record.NextRetryAt.Should().NotBeNull("an immediate handback must still read as a retry, not an orphaned submission");
        (await new ExecutionWorkerExitState(launch, store, NullLogger<ExecutionWorkerExitState>.Instance).ResolveAsync())
            .Should().Be(0);
    }

    [Theory]
    [InlineData(ExecutionJobStatus.Running, "worker-a", 1, 1)]
    [InlineData(ExecutionJobStatus.Provisioning, null, 1, 1)]
    [InlineData(ExecutionJobStatus.Running, "worker-a", 1, 2)]
    [Trait("Tier", "Fast")]
    public void ExitCode_IsNonzeroOnlyWhileThisLaunchStillOwnsTheAttemptWithoutAnOutcome(
        ExecutionJobStatus status, string? claimedBy, int launchedAttempt, int recordAttempt)
    {
        var record = SubmittedRemoteJob() with { Status = status, ClaimedBy = claimedBy, AttemptCount = recordAttempt };

        var exitCode = ExecutionWorkerExitState.Resolve(Launch() with { ExecutionAttempt = launchedAttempt }, record);

        exitCode.Should().Be(launchedAttempt == recordAttempt ? 1 : 0);
        ExecutionWorkerExitState.Resolve(Launch(), null).Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // Serving host: the heartbeat reaper leaves provider-owned claims alone
    // ---------------------------------------------------------------------

    [UnitTest]
    public async Task Reaper_DoesNotRequeueAWorkerClaimedProviderJob_IntoTheLocalQueue()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = new VersionedJobStore(SubmittedRemoteJob() with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-batch",
            ClaimedAt = stale,
            LastHeartbeatAt = stale,
        });
        var localQueue = Substitute.For<IJobQueue>();
        using var reaper = Reaper(store, localQueue);

        await reaper.SweepActiveJobsAsync(CancellationToken.None);

        store.Get(OperationId).Status.Should().Be(ExecutionJobStatus.Running);
        await localQueue.DidNotReceiveWithAnyArgs().RequeueAsync(default!);
        JobReconciliationService.IsProviderOwned(store.Get(OperationId)).Should().BeTrue();
        JobReconciliationService.IsProviderOwned(SubmittedRemoteJob() with
        {
            Spec = SubmittedRemoteJob().Spec with { Backend = LocalBatchComputeBackend.BackendId },
            ProviderOperationId = OperationId,
        }).Should().BeFalse("in-process local jobs keep heartbeat reaping");
    }

    [UnitTest]
    public async Task Reaper_StillEnforcesTheTerminalTimeout_ForProviderOwnedJobs()
    {
        var longAgo = DateTimeOffset.UtcNow.AddHours(-2);
        var store = new VersionedJobStore(SubmittedRemoteJob() with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-batch",
            ClaimedAt = longAgo,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromHours(1) },
        });
        using var reaper = Reaper(store, Substitute.For<IJobQueue>());

        await reaper.SweepActiveJobsAsync(CancellationToken.None);

        store.Get(OperationId).Status.Should().Be(ExecutionJobStatus.Failed,
            "a hung worker cannot keep a provider job Running past its timeout");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static JobReconciliationService Reaper(VersionedJobStore store, IJobQueue localQueue)
        => new(
            store,
            localQueue,
            Substitute.For<IQueueClaimReconciler>(),
            new ExecutionJobCancellationTokens(),
            [],
            null,
            NullLogger<JobReconciliationService>.Instance);

    private static async Task<(int ExitCode, bool Stopped)> RunWorkerAsync(VersionedJobStore store, IJobExecutor executor)
    {
        var launch = Launch();
        var queue = new AssignedExecutionJobQueue(launch, store, NullLogger<AssignedExecutionJobQueue>.Instance);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var stopped = false;
        lifetime.When(static l => l.StopApplication()).Do(_ => stopped = true);

        using var loop = new JobExecutionService(
            queue, store, [executor], new ExecutionJobCancellationTokens(), [], null,
            NullLogger<JobExecutionService>.Instance)
        {
            RequeueOnShutdown = true,
        };
        using var workerLifetime = new ExecutionWorkerLifetimeService(
            launch, queue, lifetime, NullLogger<ExecutionWorkerLifetimeService>.Instance);

        await loop.StartAsync(CancellationToken.None);
        await workerLifetime.StartAsync(CancellationToken.None);
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await workerLifetime.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await workerLifetime.StopAsync(CancellationToken.None);
        await loop.StopAsync(CancellationToken.None);

        var exitCode = await new ExecutionWorkerExitState(launch, store, NullLogger<ExecutionWorkerExitState>.Instance)
            .ResolveAsync();
        return (exitCode, stopped);
    }

    private static void ComposeRedisConnectedControlPlane(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Mirrors the Redis-connected serving composition of Program.cs with the real
        // registration extensions where they exist.
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<IExecutionJobStore>());
        services.AddSingleton(Substitute.For<IUniversalProgressStore>());
        services.AddHonuaLicensing(configuration, environment);
        services.AddJobOrchestration();
        services.AddJobWorker(configuration);
        services.AddHonuaBatchAndDeployBackends();
        services.AddHonuaControlPlaneReconcilers(configuration);
        services.AddSingleton<IScheduledTickDispatcher, ScheduledTickDispatcher>();
        services.AddHostedService<DeployWorkflowReconcilerBackgroundService>();
        services.AddHostedService<ExecutionJobReconcilerBackgroundService>();
        services.AddHostedService<MetadataReleaseReconcilerBackgroundService>();
        services.AddHostedService<CoordinatedReleaseReconcilerBackgroundService>();
        services.AddHostedService<ExecutionJobBackstopSweepService>();
        services.AddHostedService<WorkflowOperationBackstopSweepService>();
        services.AddHostedService<ExecutionQueueDepthCollectorBackgroundService>();
        services.AddSingleton<IOperationProposalStore, RedisOperationProposalStore>();
        services.AddSingleton<IOperationGateway, OperationGateway>();
        services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor,
            Honua.ControlPlane.Executors.DeployOperationExecutor>();
    }

    private static Type[] HostedImplementations(IServiceCollection services)
        => services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType is not null)
            .Select(static descriptor => descriptor.ImplementationType!)
            .ToArray();

    private static ExecutionWorkerLaunch Launch()
        => new()
        {
            OperationId = OperationId,
            JobKind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "Geoprocessing (AWS Batch)",
            ExecutionAttempt = 1,
        };

    private static AssignedExecutionJobQueue Queue(VersionedJobStore store)
        => new(Launch(), store, NullLogger<AssignedExecutionJobQueue>.Instance);

    private static HashSet<ExecutionJobKind> Kinds() => [ExecutionJobKind.Geoprocessing];

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    /// <summary>The record after the serving host submitted attempt 1 to AWS Batch.</summary>
    private static ExecutionJobRecord SubmittedRemoteJob(string operationId = OperationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            ProviderOperationId = operationId == OperationId ? "aws-batch-job-1" : "aws-batch-job-2",
            CurrentPhase = "Submitted AWS Batch job",
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "honua-aws-batch",
                WorkloadName = "Geoprocessing (AWS Batch)",
            },
        };
    }

    private sealed class ScriptedExecutor(
        Func<ExecutionJobRecord, IJobExecutionContext, CancellationToken, Task<JobExecutionResult>> run)
        : IJobExecutor
    {
        public List<string> ExecutedOperations { get; } = [];

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutedOperations.Add(job.OperationId);
            return await run(job, context, cancellationToken);
        }
    }

    /// <summary>An in-memory store with the version CAS the Redis store enforces.</summary>
    private sealed class VersionedJobStore(params ExecutionJobRecord[] jobs) : IExecutionJobStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ExecutionJobRecord> _jobs =
            jobs.ToDictionary(static job => job.OperationId, StringComparer.Ordinal);
        private Func<ExecutionJobRecord, ExecutionJobRecord>? _interleave;

        public ExecutionJobRecord Get(string operationId)
        {
            lock (_gate)
            {
                return _jobs[operationId];
            }
        }

        public void Replace(ExecutionJobRecord job)
        {
            lock (_gate)
            {
                _jobs[job.OperationId] = job with { Version = _jobs[job.OperationId].Version + 1 };
            }
        }

        /// <summary>Applies a concurrent write just before the next terminal CAS lands.</summary>
        public void InterleaveBeforeNextTerminalWrite(Func<ExecutionJobRecord, ExecutionJobRecord> write)
        {
            lock (_gate)
            {
                _interleave = write;
            }
        }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_jobs.TryAdd(operation.OperationId, operation));
            }
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);
            }
        }

        public Task SetAsync(ExecutionJobRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _jobs[operation.OperationId] = operation with { Version = operation.Version + 1 };
                return Task.CompletedTask;
            }
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_interleave is { } interleave && ExecutionJobReconciler.IsTerminal(job.Status)
                    && _jobs.TryGetValue(job.OperationId, out var before))
                {
                    _interleave = null;
                    _jobs[job.OperationId] = interleave(before) with { Version = before.Version + 1 };
                }

                if (!_jobs.TryGetValue(job.OperationId, out var current) || current.Version != job.Version)
                {
                    return Task.FromResult(false);
                }

                _jobs[job.OperationId] = job with { Version = job.Version + 1 };
                return Task.FromResult(true);
            }
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage { Items = [] });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
            ExecutionJobKind? kind = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_jobs.Values
                    .Where(static job => !ExecutionJobReconciler.IsTerminal(job.Status))
                    .ToArray());
            }
        }
    }
}
