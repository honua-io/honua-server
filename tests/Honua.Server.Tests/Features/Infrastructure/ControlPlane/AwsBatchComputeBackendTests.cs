// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsBatchComputeBackendTests
{
    [Fact]
    public async Task GetCapabilitiesAsync_ReportsCancellationProgressAndRetry()
    {
        var backend = CreateBackend(new StubAwsBatchJobClient());

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsCancellation.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRetry.Should().BeTrue();
        capabilities.SupportsLogStreaming.Should().BeFalse();
        capabilities.SupportsArtifactStaging.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_SubmitsJobWithResolvedParameters()
    {
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-1",
                JobArn = "arn:aws:batch:us-west-2:123:job/aws-job-1",
                JobName = "honua-op-1"
            }
        };

        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [AwsBatchParameterKeys.Region] = "us-west-2",
            [AwsBatchParameterKeys.Vcpus] = "4",
            [AwsBatchParameterKeys.MemoryMib] = "8192",
            [AwsBatchParameterKeys.TimeoutSeconds] = "3600",
            [AwsBatchParameterKeys.RetryAttempts] = "2",
            [AwsBatchParameterKeys.ShareIdentifier] = "tenant-a",
            ["env.EXTRA_FLAG"] = "value"
        });

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Queued);
        result.ProviderOperationId.Should().Be("aws-job-1");
        client.LastSubmission.Should().NotBeNull();
        client.LastSubmission!.JobDefinition.Should().Be("arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1");
        client.LastSubmission.JobQueue.Should().Be("arn:aws:batch:us-west-2:123:job-queue/gp-heavy");
        client.LastSubmission.Vcpus.Should().Be(4);
        client.LastSubmission.MemoryMib.Should().Be(8192);
        client.LastSubmission.AttemptDurationSeconds.Should().Be(3600);
        client.LastSubmission.RetryAttempts.Should().Be(2);
        client.LastSubmission.ShareIdentifier.Should().Be("tenant-a");
        client.LastRegion.Should().Be("us-west-2");
        client.LastSubmission.EnvironmentOverrides.Should()
            .Contain(entry => entry.Name == "HONUA_OPERATION_ID" && entry.Value == job.OperationId);
        client.LastSubmission.EnvironmentOverrides.Should()
            .Contain(entry => entry.Name == "HONUA_RUNTIME_PROFILE" && entry.Value == "heavy-gdal");
        client.LastSubmission.EnvironmentOverrides.Should()
            .Contain(entry => entry.Name == "EXTRA_FLAG" && entry.Value == "value");
    }

    [Fact]
    public async Task StartAsync_ThrowsWhenJobDefinitionMissing()
    {
        var backend = CreateBackend(new StubAwsBatchJobClient());
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp"
        });

        var act = async () => await backend.StartAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains(AwsBatchParameterKeys.JobDefinitionArn, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserveAsync_MapsRunningToRunning()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "RUNNING"
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.ProviderOperationId.Should().Be("aws-job-1");
    }

    [Fact]
    public async Task ObserveAsync_MapsSucceededWithReasonMessage()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "SUCCEEDED",
                StatusReason = "Essential container exited normally"
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Succeeded);
        observation.Message.Should().Contain("Essential container exited normally");
    }

    [Fact]
    public async Task ObserveAsync_MarksFailedWhenJobNotFound()
    {
        var client = new StubAwsBatchJobClient { NextDescribeResult = null };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
    }

    [Fact]
    public async Task ObserveAsync_WithoutProviderId_PassesThroughCurrentStatus()
    {
        var client = new StubAwsBatchJobClient();
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Queued);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        client.DescribeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CancelAsync_CallsCancelForQueuedJobsAndReturnsCancelledWhenProviderReachesTerminalFailure()
    {
        var client = new StubAwsBatchJobClient();
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNABLE"
        });
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "FAILED",
            StatusReason = AwsBatchStateMapper.CancelReason
        });
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        client.CancelCallCount.Should().Be(1);
        client.TerminateCallCount.Should().Be(0);
        client.DescribeCallCount.Should().Be(2);
    }

    [Fact]
    public async Task CancelAsync_CallsTerminateForRunningJobsAndReturnsNonTerminalWhileProviderTerminationIsInFlight()
    {
        var client = new StubAwsBatchJobClient();
        // AWS Batch TerminateJob is asynchronous — the job is still RUNNING on re-describe.
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNING"
        });
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNING"
        });
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("cancellation requested");
        client.CancelCallCount.Should().Be(0);
        client.TerminateCallCount.Should().Be(1);
        client.DescribeCallCount.Should().Be(2);
    }

    [Fact]
    public async Task CancelAsync_ReturnsCancelledWhenTerminateTransitionsToFailedWithCancelReason()
    {
        var client = new StubAwsBatchJobClient();
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNING"
        });
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "FAILED",
            StatusReason = AwsBatchStateMapper.CancelReason
        });
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        client.TerminateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CancelAsync_ReturnsMappedTerminalStatusWhenAlreadyComplete()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "SUCCEEDED"
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Succeeded);
        client.CancelCallCount.Should().Be(0);
        client.TerminateCallCount.Should().Be(0);
        client.DescribeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CancelAsync_ReturnsCancelledWhenProviderAlreadyFailedDueToEarlierCancel()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "FAILED",
                StatusReason = AwsBatchStateMapper.CancelReason
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        client.CancelCallCount.Should().Be(0);
        client.TerminateCallCount.Should().Be(0);
        client.DescribeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CancelAsync_WithoutProviderId_TreatsAsCancelled()
    {
        var client = new StubAwsBatchJobClient();
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        client.DescribeCallCount.Should().Be(0);
        client.CancelCallCount.Should().Be(0);
        client.TerminateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ObserveAsync_MapsFailedWithCancelReasonToCancelled()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "FAILED",
                StatusReason = AwsBatchStateMapper.CancelReason
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [Fact]
    public async Task ObserveAsync_MapsFailedWithRealWorkloadFailureToFailed()
    {
        var client = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-1",
                Status = "FAILED",
                StatusReason = "Container exited with exit code 137"
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
    }

    private static AwsBatchComputeBackend CreateBackend(IAwsBatchJobClient client)
        => new(client, NullLogger<AwsBatchComputeBackend>.Instance);

    internal static ExecutionJobRecord CreateJob(
        IReadOnlyDictionary<string, string>? parameters = null,
        string? providerOperationId = null,
        ExecutionJobStatus status = ExecutionJobStatus.Queued)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"gp-{Guid.NewGuid():N}",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ProviderOperationId = providerOperationId,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "honua-aws-batch",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "heavy-gdal-clip",
                WorkloadId = "heavy-gdal-clip",
                RuntimeProfile = "heavy-gdal",
                Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
                    [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy"
                }
            }
        };
    }
}

internal sealed class StubAwsBatchJobClient : IAwsBatchJobClient
{
    public AwsBatchSubmitResult NextSubmitResult { get; set; } = new()
    {
        JobId = "stub-job-id",
        JobArn = "arn:aws:batch:test:123:job/stub",
        JobName = "stub-job"
    };

    public AwsBatchJobState? NextDescribeResult { get; set; } = new()
    {
        JobId = "stub-job-id",
        Status = "RUNNING"
    };

    /// <summary>
    /// Optional queue of scripted describe results. Each describe call dequeues the next
    /// entry; once empty, <see cref="NextDescribeResult"/> is returned. Lets tests model
    /// sequential AWS Batch state transitions (for example, RUNNABLE then FAILED after a
    /// cancel request).
    /// </summary>
    public Queue<AwsBatchJobState?> DescribeResults { get; } = new();

    public AwsBatchJobSubmission? LastSubmission { get; private set; }

    public string? LastRegion { get; private set; }

    public int CancelCallCount { get; private set; }

    public int TerminateCallCount { get; private set; }

    public int DescribeCallCount { get; private set; }

    public Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        CancellationToken cancellationToken = default)
    {
        LastSubmission = submission;
        LastRegion = region;
        return Task.FromResult(NextSubmitResult);
    }

    public Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        CancellationToken cancellationToken = default)
    {
        DescribeCallCount++;
        LastRegion = region;
        var result = DescribeResults.Count > 0 ? DescribeResults.Dequeue() : NextDescribeResult;
        return Task.FromResult(result);
    }

    public Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        CancelCallCount++;
        return Task.CompletedTask;
    }

    public Task TerminateJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        TerminateCallCount++;
        return Task.CompletedTask;
    }
}
