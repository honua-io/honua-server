// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AzureBatchComputeBackendTests
{
    [Fact]
    public async Task StartAsync_SubmitsJobWithPoolAndImage()
    {
        var stub = new StubAzureBatchClient();
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob(
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["azure.batch.account_url"] = "https://acct.eastus.batch.azure.com",
                ["azure.batch.pool_id"] = "gdal-heavy-pool",
                ["azure.batch.container_image"] = "ghcr.io/honua-io/gdal-worker:2026.04",
                ["azure.storage.output_container_url"] = "https://acct.blob.core.windows.net/artifacts?sv=..."
            }));

        submission.Status.Should().Be(ExecutionJobStatus.Queued);
        submission.ProviderOperationId.Should().StartWith("honua-");
        stub.LastSubmission.Should().NotBeNull();
        stub.LastSubmission!.PoolId.Should().Be("gdal-heavy-pool");
        stub.LastSubmission.ContainerImage.Should().Be("ghcr.io/honua-io/gdal-worker:2026.04");
        stub.LastSubmission.OutputContainerUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAsync_TreatsConflictAsIdempotentSuccess()
    {
        var stub = new StubAzureBatchClient { ReturnStatus = HttpStatusCode.Conflict };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Queued);
        submission.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task StartAsync_FailsWhenRequiredParametersMissing()
    {
        var stub = new StubAzureBatchClient();
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        Func<Task> act = () => backend.StartAsync(CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("azure.batch.account_url", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_ReturnsFailedWhenBackendRejectsSubmission()
    {
        var stub = new StubAzureBatchClient
        {
            SubmissionException = new HttpRequestException("account denied", null, HttpStatusCode.Forbidden)
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("account denied");
        // Preserve the deterministic JobId so a late-accepted job can still be observed and
        // cancelled during reconciliation rather than orphaned at the provider.
        submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();
        submission.ProviderOperationId.Should().StartWith("honua-");
    }

    [Fact]
    public async Task StartAsync_KeepsAmbiguousSubmitActiveForReconciliation()
    {
        var stub = new StubAzureBatchClient
        {
            SubmissionException = new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout)
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Queued,
            "transport-ambiguous submit failures must stay active so reconciliation can verify provider ownership");
        submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();
        submission.Message.Should().Contain("outcome is uncertain");
        submission.Message.Should().Contain("Reconciliation will verify");
    }

    [Fact]
    public async Task StartAsync_ForwardsRuntimeProfileEnvVariable()
    {
        var stub = new StubAzureBatchClient();
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        await backend.StartAsync(CreateJob(runtimeProfile: "gdal-heavy"));

        stub.LastSubmission!.EnvironmentSettings.Should().ContainKey("HONUA_RUNTIME_PROFILE")
            .WhoseValue.Should().Be("gdal-heavy");
        stub.LastSubmission.EnvironmentSettings.Should().ContainKey("HONUA_JOB_ID");
        stub.LastSubmission.EnvironmentSettings.Should().ContainKey("HONUA_WORKLOAD_NAME");
    }

    [Fact]
    public async Task StartAsync_DefaultCommandLineDoesNotInterpolateWorkloadName()
    {
        // Human-readable workload names (e.g. "Python GP") must not be string-interpolated
        // into the default shell command line: shell word-splitting would split the name
        // across arguments and break the worker invocation. Use env-var expansion instead.
        var stub = new StubAzureBatchClient();
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        await backend.StartAsync(CreateJob(workloadName: "Python GP"));

        stub.LastSubmission!.CommandLine.Should().NotContain("Python GP",
            "workload name must not be substituted into the shell command line verbatim");
        stub.LastSubmission.CommandLine.Should().Contain("$HONUA_WORKLOAD_NAME",
            "the default command line must resolve the workload name via environment variable expansion");
        stub.LastSubmission.EnvironmentSettings["HONUA_WORKLOAD_NAME"].Should().Be("Python GP");
    }

    [Fact]
    public async Task StartAsync_FailsFastWhenPoolHasNoCurrentOrTargetNodes()
    {
        // Pool with no current or target nodes would never schedule a task: fail fast so the
        // durable record surfaces an actionable submission error instead of silently waiting
        // in `preparing` for an operator to notice.
        var stub = new StubAzureBatchClient
        {
            NextPoolState = new AzureBatchPoolState
            {
                PoolId = "empty-pool",
                State = "active",
                AllocationState = "steady",
                DedicatedNodes = 0,
                LowPriorityNodes = 0,
                TargetDedicatedNodes = 0,
                TargetLowPriorityNodes = 0
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("no current or target nodes");
        stub.LastSubmission.Should().BeNull("pool validation must fail before submission");
    }

    [Fact]
    public async Task StartAsync_SubmitsWhenPoolIsResizingTowardTargetNodes()
    {
        // Resizing toward non-zero target nodes is valid: tasks queue briefly until nodes
        // come up. Only empty-and-not-resizing pools are a hard failure.
        var stub = new StubAzureBatchClient
        {
            NextPoolState = new AzureBatchPoolState
            {
                PoolId = "resizing-pool",
                State = "active",
                AllocationState = "resizing",
                DedicatedNodes = 0,
                TargetDedicatedNodes = 2
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Queued);
        stub.LastSubmission.Should().NotBeNull();
    }

    [Fact]
    public async Task StartAsync_FailsWhenPoolIsDeletingOrNotActive()
    {
        var stub = new StubAzureBatchClient
        {
            NextPoolState = new AzureBatchPoolState
            {
                PoolId = "deleting-pool",
                State = "deleting",
                AllocationState = "stopping",
                DedicatedNodes = 3
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("deleting");
        stub.LastSubmission.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_FailsWithDescriptiveErrorWhenPoolLookupRejects()
    {
        // Missing pool (404) surfaces as HttpRequestException. Validation must convert that
        // into a descriptive submission failure so the operator knows to reconfigure.
        var stub = new StubAzureBatchClient
        {
            PoolStateException = new HttpRequestException("pool not found")
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("not reachable");
        submission.Message.Should().Contain("pool not found");
    }

    [Theory]
    [InlineData("active", 0, false, ExecutionJobStatus.Queued)]
    [InlineData("preparing", 0, false, ExecutionJobStatus.Provisioning)]
    [InlineData("running", 0, false, ExecutionJobStatus.Running)]
    [InlineData("completed", 0, false, ExecutionJobStatus.Succeeded)]
    [InlineData("completed", 1, false, ExecutionJobStatus.Failed)]
    [InlineData("completed", 0, true, ExecutionJobStatus.Failed)]
    public async Task ObserveAsync_MapsAzureBatchStateToCanonicalStatus(
        string rawState,
        int exitCode,
        bool hasFailure,
        ExecutionJobStatus expected)
    {
        var stateToReturn = new AzureBatchJobState
        {
            JobId = "honua-test",
            ExecutionState = rawState.ToLowerInvariant() switch
            {
                "active" => AzureBatchTaskExecutionState.Active,
                "preparing" => AzureBatchTaskExecutionState.Preparing,
                "running" => AzureBatchTaskExecutionState.Running,
                "completed" when exitCode == 0 && !hasFailure => AzureBatchTaskExecutionState.CompletedSuccess,
                _ => AzureBatchTaskExecutionState.CompletedFailure
            },
            RawTaskState = rawState,
            ExitCode = exitCode,
            FailureMessage = hasFailure ? "node preempted" : null
        };

        var stub = new StubAzureBatchClient { NextState = stateToReturn };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateJob(providerJobId: "honua-test"));

        observation.Status.Should().Be(expected);
        observation.ProviderOperationId.Should().Be("honua-test");
    }

    [Fact]
    public async Task ObserveAsync_PreservesCancelledIntentWhenBatchReportsFailure()
    {
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.CompletedFailure,
                RawTaskState = "completed",
                ExitCode = null,
                FailureMessage = "Task terminated"
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var cancelledJob = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Cancelled
        };

        var observation = await backend.ObserveAsync(cancelledJob);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [Fact]
    public async Task ObserveAsync_TreatsNotFoundAsQueued()
    {
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateJob(providerJobId: "honua-test"));

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
    }

    [Fact]
    public async Task ObserveAsync_NotFoundBeforeGracePreservesRunningState()
    {
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Running,
            AttemptCount = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running,
            "a transient provider 404 must not demote an already-running job back to queued");
    }

    [Fact]
    public async Task ObserveAsync_NotFoundPastGraceFailsSubmittedJob()
    {
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Queued,
            AttemptCount = 1,
            UpdatedAt = DateTimeOffset.UtcNow - AzureBatchComputeBackend.MissingRegistrationGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.PercentComplete.Should().Be(100);
        observation.Message.Should().Contain("did not register with the scheduler");
    }

    [Fact]
    public async Task ObserveAsync_NotFoundPastGraceWithCancellationRequested_CancelsJob()
    {
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Queued,
            AttemptCount = 1,
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow - AzureBatchComputeBackend.MissingRegistrationGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.PercentComplete.Should().Be(100);
        observation.Message.Should().Contain("never registered before cancellation completed");
    }

    [Fact]
    public async Task CancelAsync_TerminatesBatchJobAndReportsTerminationInProgress()
    {
        // Azure Batch `Job - Terminate` returns 202 Accepted and the job first enters a
        // `terminating` phase before `completed`. The adapter must keep the observation
        // nonterminal until Batch confirms termination so the reconciler does not stamp
        // the record Cancelled/100% while Batch is still draining the task.
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.Running,
                RawTaskState = "running"
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Running,
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running,
            "terminate has been accepted but Batch has not yet drained the task");
        observation.Message.Should().Contain("termination requested", Exactly.Once());
        stub.TerminatedJobs.Should().ContainSingle().Which.Should().Be("honua-test");
    }

    [Fact]
    public async Task CancelAsync_ReportsCancelledOnceBatchCompletesTermination()
    {
        // Once Batch finishes terminating, the task reaches CompletedFailure. Because the
        // durable record carries CancellationRequestedAt, MapObservation routes the failure
        // to Cancelled instead of Failed — the cancellation intent is preserved.
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.CompletedFailure,
                RawTaskState = "completed",
                FailureMessage = "Task was terminated by request"
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Running,
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.PercentComplete.Should().Be(100);
        stub.TerminatedJobs.Should().ContainSingle().Which.Should().Be("honua-test");
    }

    [Fact]
    public async Task CancelAsync_IsIdempotentAcrossRetries()
    {
        // The reconciler will call CancelAsync on every cycle while CancellationRequestedAt
        // is set. Repeated TerminateJobAsync calls must be safe (idempotent).
        var stub = new StubAzureBatchClient
        {
            NextState = new AzureBatchJobState
            {
                JobId = "honua-test",
                ExecutionState = AzureBatchTaskExecutionState.Running,
                RawTaskState = "running"
            }
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with
        {
            Status = ExecutionJobStatus.Running,
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };

        await backend.CancelAsync(job);
        await backend.CancelAsync(job);

        stub.TerminatedJobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancelAsync_PreservesCurrentStatusWhenTerminateThrows()
    {
        var stub = new StubAzureBatchClient
        {
            TerminateException = new HttpRequestException("service unavailable")
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var job = CreateJob(providerJobId: "honua-test") with { Status = ExecutionJobStatus.Running };

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("service unavailable");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_AdvertisesCancellationRetryAndArtifactStaging()
    {
        var backend = new AzureBatchComputeBackend(new StubAzureBatchClient(), NullLogger<AzureBatchComputeBackend>.Instance);

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsCancellation.Should().BeTrue();
        capabilities.SupportsRetry.Should().BeTrue();
        capabilities.SupportsArtifactStaging.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsLogStreaming.Should().BeFalse();
    }

    private static ExecutionJobRecord CreateJob(
        string? providerJobId = null,
        string? runtimeProfile = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        string workloadName = "test-workload")
    {
        var operationId = $"job-{Guid.NewGuid():N}";
        parameters ??= new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["azure.batch.account_url"] = "https://acct.eastus.batch.azure.com",
            ["azure.batch.pool_id"] = "default-pool"
        };

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderOperationId = providerJobId,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AzureBatch,
                Backend = AzureBatchComputeBackend.BackendIdentifier,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = workloadName,
                WorkloadId = "wl-1",
                RuntimeProfile = runtimeProfile,
                Parameters = parameters
            }
        };
    }

    private sealed class StubAzureBatchClient : IAzureBatchClient
    {
        public HttpStatusCode ReturnStatus { get; set; } = HttpStatusCode.Created;

        public AzureBatchJobSubmission? LastSubmission { get; private set; }

        public AzureBatchJobState? NextState { get; set; }

        public AzureBatchPoolState? NextPoolState { get; set; }

        public HttpRequestException? SubmissionException { get; set; }

        public HttpRequestException? TerminateException { get; set; }

        public HttpRequestException? PoolStateException { get; set; }

        public List<string> TerminatedJobs { get; } = [];

        public Task<HttpStatusCode> CreateJobAsync(
            AzureBatchJobSubmission submission,
            CancellationToken cancellationToken = default)
        {
            if (SubmissionException != null)
            {
                throw SubmissionException;
            }

            LastSubmission = submission;
            return Task.FromResult(ReturnStatus);
        }

        public Task<AzureBatchJobState> GetJobStateAsync(
            string accountUrl,
            string jobId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(NextState ?? new AzureBatchJobState
            {
                JobId = jobId,
                ExecutionState = AzureBatchTaskExecutionState.NotFound
            });

        public Task TerminateJobAsync(
            string accountUrl,
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (TerminateException != null)
            {
                throw TerminateException;
            }

            TerminatedJobs.Add(jobId);
            return Task.CompletedTask;
        }

        public Task<AzureBatchPoolState> GetPoolStateAsync(
            string accountUrl,
            string poolId,
            CancellationToken cancellationToken = default)
        {
            if (PoolStateException != null)
            {
                throw PoolStateException;
            }

            return Task.FromResult(NextPoolState ?? new AzureBatchPoolState
            {
                PoolId = poolId,
                State = "active",
                AllocationState = "steady",
                DedicatedNodes = 1,
                TargetDedicatedNodes = 1
            });
        }
    }
}
