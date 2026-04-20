// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Net;
using Amazon.Runtime;
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

    [Fact]
    public async Task StartAsync_RecordsSubmissionMetricOnFreshSubmissions()
    {
        var samples = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Name == "honua.execution.job.submitted")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => samples.Add(measurement));
        listener.Start();

        var backend = CreateBackend(new StubAwsBatchJobClient());

        await backend.StartAsync(CreateJob());

        samples.Should().ContainSingle().Which.Should().Be(1,
            "AWS Batch must emit the standard submission counter so execution dashboards pick up every backend");
    }

    [Fact]
    public async Task StartAsync_DoesNotRecordSubmissionMetricOnRejectedSubmission()
    {
        var samples = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Name == "honua.execution.job.submitted")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => samples.Add(measurement));
        listener.Start();

        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("invalid job definition")
            {
                StatusCode = HttpStatusCode.BadRequest
            }
        };
        var backend = CreateBackend(client);

        await backend.StartAsync(CreateJob());

        samples.Should().BeEmpty("rejected submissions must not inflate the fresh-submission counter");
    }

    [Fact]
    public async Task StartAsync_KeepsAmbiguousSubmitQueuedWithPendingMarker()
    {
        // Transport-ambiguous submit failures (5xx/429/408/credential/network) may still
        // have been accepted by AWS Batch. The adapter must preserve a stable lookup key so
        // reconciliation can verify ownership instead of stamping the durable record Failed.
        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("service unavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob();

        var submission = await backend.StartAsync(job);

        submission.Status.Should().Be(ExecutionJobStatus.Queued,
            "transport-ambiguous submit failures must stay active so reconciliation can verify provider ownership");
        submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();
        submission.ProviderOperationId.Should().StartWith(AwsBatchComputeBackend.PendingSubmissionMarkerPrefix);
        submission.Message.Should().Contain("outcome is uncertain");
        submission.Message.Should().Contain("Reconciliation will verify");
    }

    [Fact]
    public async Task StartAsync_KeepsAmbiguousSubmitQueuedOnCredentialFailure()
    {
        // Credential acquisition failures surface as AmazonServiceException with a zero
        // status code (no HTTP response). Treat as ambiguous so the next reconciliation
        // cycle can either retry or discover any job the provider did accept.
        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("credential acquisition failed: token expired")
        };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Queued);
        submission.ProviderOperationId.Should().StartWith(AwsBatchComputeBackend.PendingSubmissionMarkerPrefix);
        submission.Message.Should().Contain("credential acquisition failed");
    }

    [Fact]
    public async Task StartAsync_ReturnsFailedWhenBackendRejectsSubmission()
    {
        // Definite provider rejection (4xx with a real status code) is a misconfiguration:
        // fail fast with an actionable message instead of leaving the job in an indefinite
        // retry loop.
        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("job definition revision is not registered")
            {
                StatusCode = HttpStatusCode.BadRequest
            }
        };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Failed);
        submission.Message.Should().Contain("job definition revision is not registered");
    }

    [Fact]
    public async Task ObserveAsync_PreservesCurrentStatusWhenDescribeThrowsProviderException()
    {
        // Regression for finding: a credential/transport failure during observe must not
        // escape the backend. If it did, the reconciler's generic catch would stamp the
        // durable record terminal Failed while the AWS Batch job could still be running.
        var client = new StubAwsBatchJobClient
        {
            DescribeException = new AmazonServiceException("credential acquisition failed: token expired")
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running,
            "credential failures during observe must preserve durable state for the next reconciliation cycle");
        observation.ProviderOperationId.Should().Be("aws-job-1");
        observation.Message.Should().Contain("observation failed");
        observation.Message.Should().Contain("credential acquisition failed");
    }

    [Fact]
    public async Task CancelAsync_PreservesCurrentStatusWhenDescribeThrowsProviderException()
    {
        // Regression for finding: a credential/transport failure during cancel must not
        // escape the backend. The cancel endpoints wrap this call directly, so an uncaught
        // exception would surface as an unhandled API failure to the user.
        var client = new StubAwsBatchJobClient
        {
            DescribeException = new AmazonServiceException("service unavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.ProviderOperationId.Should().Be("aws-job-1");
        observation.Message.Should().Contain("cancellation failed");
    }

    [Fact]
    public async Task CancelAsync_PreservesCurrentStatusWhenCancelJobThrows()
    {
        // Describe succeeds but the subsequent CancelJob call fails. The durable record
        // must stay nonterminal so the reconciler can retry on the next cycle.
        var client = new StubAwsBatchJobClient();
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNABLE"
        });
        client.CancelException = new AmazonServiceException("throttled")
        {
            StatusCode = HttpStatusCode.TooManyRequests
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        observation.Message.Should().Contain("cancellation failed");
    }

    [Fact]
    public async Task CancelAsync_PreservesCurrentStatusWhenTerminateJobThrows()
    {
        var client = new StubAwsBatchJobClient();
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-1",
            Status = "RUNNING"
        });
        client.TerminateException = new AmazonServiceException("service unavailable")
        {
            StatusCode = HttpStatusCode.ServiceUnavailable
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("cancellation failed");
    }

    [Fact]
    public async Task ObserveAsync_ResolvesPendingMarkerToRealJobIdViaListJobs()
    {
        // Happy path for the pending-discovery flow: a previously ambiguous submit left the
        // record with a pending marker; reconciliation's next observe call must resolve the
        // marker to the real provider JobId when ListJobs returns a match.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult =
            [
                new AwsBatchJobState
                {
                    JobId = "aws-job-discovered",
                    Status = "RUNNABLE"
                }
            ]
        };
        var backend = CreateBackend(client);
        var job = CreateJob(
            providerOperationId: AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1",
            status: ExecutionJobStatus.Queued);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        observation.ProviderOperationId.Should().Be("aws-job-discovered");
        observation.Message.Should().Contain("Discovered AWS Batch job");
        client.ListJobsCallCount.Should().Be(1);
        client.LastListJobsName.Should().Be("honua-op-1");
    }

    [Fact]
    public async Task ObserveAsync_KeepsPendingMarkerWhenListJobsReturnsEmpty()
    {
        // If discovery cannot find the job yet, the record must stay in its current
        // (nonterminal) state so the next reconciliation cycle can retry discovery.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        observation.ProviderOperationId.Should().Be(pendingId);
        observation.Message.Should().Contain("still being verified");
    }

    [Fact]
    public async Task ObserveAsync_PreservesStatusWhenPendingDiscoveryThrowsProviderException()
    {
        var client = new StubAwsBatchJobClient
        {
            ListJobsException = new AmazonServiceException("service unavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            }
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        observation.ProviderOperationId.Should().Be(pendingId);
        observation.Message.Should().Contain("discovery failed");
    }

    [Fact]
    public async Task CancelAsync_WithPendingMarker_ReturnsCancelledWhenListJobsEmpty()
    {
        // Cancelling a pending-but-never-acknowledged submission: if AWS has no record of
        // the job, treat as cancelled-before-submission rather than leaving the record in
        // an indeterminate state.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.Message.Should().Contain("not discoverable");
    }

    [Fact]
    public async Task CancelAsync_WithPendingMarker_DelegatesToNormalPathWhenDiscovered()
    {
        // If ListJobs resolves the pending marker to a real provider job, the cancel flow
        // must take the normal describe/cancel/terminate path against the real JobId.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult =
            [
                new AwsBatchJobState
                {
                    JobId = "aws-job-discovered",
                    Status = "RUNNABLE"
                }
            ]
        };
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-discovered",
            Status = "RUNNABLE"
        });
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-discovered",
            Status = "FAILED",
            StatusReason = AwsBatchStateMapper.CancelReason
        });
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.ProviderOperationId.Should().Be("aws-job-discovered");
        client.CancelCallCount.Should().Be(1);
    }

    [Fact]
    public void TryExtractPendingJobName_RecognizesMarkerAndExtractsName()
    {
        AwsBatchComputeBackend.TryExtractPendingJobName(
            AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1",
            out var pendingName).Should().BeTrue();
        pendingName.Should().Be("honua-op-1");

        AwsBatchComputeBackend.TryExtractPendingJobName("aws-job-123", out pendingName).Should().BeFalse();
        pendingName.Should().BeEmpty();

        AwsBatchComputeBackend.TryExtractPendingJobName(null, out pendingName).Should().BeFalse();
        AwsBatchComputeBackend.TryExtractPendingJobName(
            AwsBatchComputeBackend.PendingSubmissionMarkerPrefix,
            out pendingName).Should().BeFalse();
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

    /// <summary>
    /// Optional result for <see cref="ListJobsByNameAsync"/>. Null means the stub should
    /// return the default empty list; tests set this to script discovery outcomes.
    /// </summary>
    public IReadOnlyList<AwsBatchJobState>? NextListJobsResult { get; set; }

    public AmazonServiceException? SubmitException { get; set; }

    public AmazonServiceException? DescribeException { get; set; }

    public AmazonServiceException? ListJobsException { get; set; }

    public AmazonServiceException? CancelException { get; set; }

    public AmazonServiceException? TerminateException { get; set; }

    public AwsBatchJobSubmission? LastSubmission { get; private set; }

    public string? LastRegion { get; private set; }

    public string? LastListJobsQueue { get; private set; }

    public string? LastListJobsName { get; private set; }

    public int CancelCallCount { get; private set; }

    public int TerminateCallCount { get; private set; }

    public int DescribeCallCount { get; private set; }

    public int ListJobsCallCount { get; private set; }

    public Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        CancellationToken cancellationToken = default)
    {
        LastSubmission = submission;
        LastRegion = region;
        if (SubmitException != null)
        {
            throw SubmitException;
        }

        return Task.FromResult(NextSubmitResult);
    }

    public Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        CancellationToken cancellationToken = default)
    {
        DescribeCallCount++;
        LastRegion = region;
        if (DescribeException != null)
        {
            throw DescribeException;
        }

        var result = DescribeResults.Count > 0 ? DescribeResults.Dequeue() : NextDescribeResult;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AwsBatchJobState>> ListJobsByNameAsync(
        string jobQueue,
        string jobName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        ListJobsCallCount++;
        LastListJobsQueue = jobQueue;
        LastListJobsName = jobName;
        LastRegion = region;
        if (ListJobsException != null)
        {
            throw ListJobsException;
        }

        return Task.FromResult<IReadOnlyList<AwsBatchJobState>>(
            NextListJobsResult ?? Array.Empty<AwsBatchJobState>());
    }

    public Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        CancelCallCount++;
        if (CancelException != null)
        {
            throw CancelException;
        }

        return Task.CompletedTask;
    }

    public Task TerminateJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        TerminateCallCount++;
        if (TerminateException != null)
        {
            throw TerminateException;
        }

        return Task.CompletedTask;
    }
}
