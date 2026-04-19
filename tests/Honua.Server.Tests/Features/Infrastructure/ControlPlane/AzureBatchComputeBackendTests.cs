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
            SubmissionException = new HttpRequestException("account denied")
        };
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("account denied");
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
    public async Task CancelAsync_TerminatesBatchJobAndReportsCancelled()
    {
        var stub = new StubAzureBatchClient();
        var backend = new AzureBatchComputeBackend(stub, NullLogger<AzureBatchComputeBackend>.Instance);

        var observation = await backend.CancelAsync(CreateJob(providerJobId: "honua-test"));

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.PercentComplete.Should().Be(100);
        stub.TerminatedJobs.Should().ContainSingle().Which.Should().Be("honua-test");
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
        IReadOnlyDictionary<string, string>? parameters = null)
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
                WorkloadName = "test-workload",
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
            => Task.FromResult(NextPoolState ?? new AzureBatchPoolState { PoolId = poolId });
    }
}
