// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Amazon.Runtime;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.ControlPlane;
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
        capabilities.MaxSupportedContractVersion.Should().Be(RasterOutputContract.JobContractVersion);
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
            ["env.EXTRA_FLAG"] = "value",
            // Custom-code param->env contract (#2191): env.CUSTOMCODE_* keys the
            // submit coordinator projects must surface to the container verbatim.
            ["env.CUSTOMCODE_REPO_URL"] = "https://github.com/honua-io/example.git"
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
        client.LastSubmission.EnvironmentOverrides.Should()
            .Contain(entry => entry.Name == "CUSTOMCODE_REPO_URL"
                && entry.Value == "https://github.com/honua-io/example.git");
        // Serving↔worker job-contract version (ADR-0060 #3b) is injected so the worker can re-check it.
        client.LastSubmission.EnvironmentOverrides.Should()
            .Contain(entry => entry.Name == "HONUA_CONTRACT_VERSION" && entry.Value == "1");
    }

    [Fact]
    public async Task StartAsync_WorkloadCannotOverrideContractVersionGate()
    {
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-gate",
                JobArn = "arn:aws:batch:us-west-2:123:job/aws-job-gate",
                JobName = "honua-op-gate"
            }
        };

        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            // A malicious/erroneous workload tries to weaken the serving↔worker contract gate.
            ["env.HONUA_CONTRACT_VERSION"] = "999"
        });

        await backend.StartAsync(job);

        client.LastSubmission.Should().NotBeNull();
        client.LastSubmission!.EnvironmentOverrides
            .Where(entry => entry.Name == "HONUA_CONTRACT_VERSION")
            .Should().OnlyContain(entry => entry.Value == "1",
            "the stamped contract-version gate must win over a workload env.HONUA_CONTRACT_VERSION passthrough.");
    }

    [Fact]
    public async Task StartAsync_RasterOutputContractProjectsOnlyStableAttemptScopedMetadata()
    {
        var client = new StubAwsBatchJobClient();
        var backend = CreateBackend(client);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [RasterOutputWorkerContract.StoreReferenceParameter] = "gp-results",
            ["env.HONUA_RASTER_OUTPUT_STORE_REFERENCE"] = "https://attacker.example/signed?secret=yes",
            ["env.HONUA_RASTER_OUTPUT_STAGING_PREFIX"] = "raster/staging/wrong/attempt-0/",
            ["env.HONUA_RASTER_OUTPUT_MANIFEST_KEY"] = "credential=leak",
            ["env.HONUA_RASTER_OUTPUT_CONTRACT_VERSION"] = "999"
        };
        var initial = CreateJob(parameters);
        var job = initial with
        {
            AttemptCount = 3,
            Spec = initial.Spec with { ContractVersion = RasterOutputContract.JobContractVersion }
        };

        await backend.StartAsync(job);

        var environment = client.LastSubmission!.EnvironmentOverrides;
        environment.Where(entry => entry.Name == RasterOutputWorkerContract.StoreReferenceEnvironmentVariable)
            .Should().ContainSingle().Which.Value.Should().Be("gp-results");
        environment.Where(entry => entry.Name == RasterOutputWorkerContract.StagingPrefixEnvironmentVariable)
            .Should().ContainSingle().Which.Value.Should().Be(
                RasterOutputWorkerContract.BuildStagingPrefix(job.OperationId, 3));
        environment.Where(entry => entry.Name == RasterOutputWorkerContract.ManifestKeyEnvironmentVariable)
            .Should().ContainSingle().Which.Value.Should().Be(
                RasterOutputWorkerContract.BuildManifestObjectKey(job.OperationId, 3));
        environment.Where(entry => entry.Name == RasterOutputWorkerContract.ContractVersionEnvironmentVariable)
            .Should().ContainSingle().Which.Value.Should().Be(
                RasterOutputContract.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        environment.Should().NotContain(entry => entry.Value.Contains("attacker", StringComparison.OrdinalIgnoreCase)
            || entry.Value.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || entry.Value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_RasterOutputContractRejectsUrlStoreReference()
    {
        var backend = CreateBackend(new StubAwsBatchJobClient());
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [RasterOutputWorkerContract.StoreReferenceParameter] = "https://bucket.example/signed?token=secret"
        };
        var initial = CreateJob(parameters);
        var job = initial with
        {
            Spec = initial.Spec with { ContractVersion = RasterOutputContract.JobContractVersion }
        };

        var action = async () => await backend.StartAsync(job);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*logical store reference*");
    }

    [Fact]
    public async Task StartAsync_SelectsTierJobDefinitionFromEphemeralHint()
    {
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-tier",
                JobArn = "arn:aws:batch:us-west-2:123:job/aws-job-tier",
                JobName = "honua-op-tier"
            }
        };

        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "s"] = "arn:aws:batch:us-west-2:123:job-definition/gp-s:1",
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "m"] = "arn:aws:batch:us-west-2:123:job-definition/gp-m:1",
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "l"] = "arn:aws:batch:us-west-2:123:job-definition/gp-l:1",
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "xl"] = "arn:aws:batch:us-west-2:123:job-definition/gp-xl:1",
            [AwsBatchParameterKeys.EphemeralGib] = "75",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp",
            [AwsBatchParameterKeys.Vcpus] = "4",
            [AwsBatchParameterKeys.MemoryMib] = "8192"
        });

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Queued);
        client.LastSubmission.Should().NotBeNull();
        // 75 GiB falls in the (50,100] band -> 'l' tier.
        client.LastSubmission!.JobDefinition.Should().Be("arn:aws:batch:us-west-2:123:job-definition/gp-l:1");
        // vCPU/memory remain SubmitJob overrides regardless of tier selection.
        client.LastSubmission.Vcpus.Should().Be(4);
        client.LastSubmission.MemoryMib.Should().Be(8192);
        // The tier and ephemeral keys are batch.* params and must not leak as env overrides.
        client.LastSubmission.EnvironmentOverrides.Should()
            .NotContain(entry => entry.Name.Contains("job_definition_arn") || entry.Name.Contains("ephemeral"));
    }

    [Fact]
    public async Task StartAsync_DefaultsToSmallestTier_WhenNoEphemeralHint()
    {
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-tier-default",
                JobArn = "arn:aws:batch:us-west-2:123:job/aws-job-tier-default",
                JobName = "honua-op-tier-default"
            }
        };

        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "s"] = "arn:aws:batch:us-west-2:123:job-definition/gp-s:1",
            [AwsBatchParameterKeys.JobDefinitionArnTierPrefix + "xl"] = "arn:aws:batch:us-west-2:123:job-definition/gp-xl:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp"
        });

        await backend.StartAsync(job);

        client.LastSubmission!.JobDefinition.Should().Be("arn:aws:batch:us-west-2:123:job-definition/gp-s:1");
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
    public async Task StartAsync_WithServiceUrlParameter_ForwardsEndpointOverride()
    {
        // An opt-in batch.service_url parameter (LocalStack Pro / real-cloud Batch endpoint) must
        // reach the SDK client seam as a ServiceURL override.
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-1",
                JobName = "honua-op-1"
            }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [AwsBatchParameterKeys.Region] = "us-west-2",
            [AwsBatchParameterKeys.ServiceUrl] = "http://localhost:4566"
        });

        await backend.StartAsync(job);

        client.LastServiceUrl.Should().Be("http://localhost:4566");
    }

    [Fact]
    public async Task StartAsync_WithoutServiceUrlParameter_LeavesEndpointOverrideUnset()
    {
        var client = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult { JobId = "aws-job-1", JobName = "honua-op-1" }
        };
        var backend = CreateBackend(client);
        var job = CreateJob(parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AwsBatchParameterKeys.JobDefinitionArn] = "arn:aws:batch:us-west-2:123:job-definition/heavy-gdal:1",
            [AwsBatchParameterKeys.JobQueueArn] = "arn:aws:batch:us-west-2:123:job-queue/gp-heavy",
            [AwsBatchParameterKeys.Region] = "us-west-2"
        });

        await backend.StartAsync(job);

        client.LastServiceUrl.Should().BeNull();
    }

    [Fact]
    public void CreateClient_WithServiceUrl_AppliesEndpointOverride()
    {
        using var client = AwsSdkBatchJobClient.CreateClient("us-west-2", "http://localhost:4566");

        // The AWS SDK treats ServiceURL and RegionEndpoint as mutually exclusive: setting an
        // explicit ServiceURL clears RegionEndpoint (mirrors AwsS3FileStorage). The override must
        // win so the client targets the emulator endpoint.
        client.Config.ServiceURL.Should().Be("http://localhost:4566/");
        client.Config.RegionEndpoint.Should().BeNull();
    }

    [Fact]
    public void CreateClient_WithoutServiceUrl_UsesDefaultRegionalEndpoint()
    {
        using var client = AwsSdkBatchJobClient.CreateClient("us-west-2", serviceUrl: null);

        client.Config.RegionEndpoint!.SystemName.Should().Be("us-west-2");
        string.IsNullOrEmpty(client.Config.ServiceURL).Should().BeTrue();
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
        const string metricBackend = "honua-aws-batch-metric-fresh-test";
        var samples = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Name == ControlPlaneTelemetry.Metrics.ExecutionJobSubmitted)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? backend = null;
            foreach (var tag in tags)
            {
                if (tag.Key == ControlPlaneTelemetry.Tags.Backend)
                {
                    backend = tag.Value as string;
                    break;
                }
            }

            if (!string.Equals(backend, metricBackend, StringComparison.Ordinal))
            {
                return;
            }

            lock (samples)
            {
                samples.Add(measurement);
            }
        });
        listener.Start();

        var backend = CreateBackend(new StubAwsBatchJobClient());

        await backend.StartAsync(CreateJob(backend: metricBackend));

        long[] sampleSnapshot;
        lock (samples)
        {
            sampleSnapshot = samples.ToArray();
        }

        sampleSnapshot.Should().ContainSingle().Which.Should().Be(1,
            "AWS Batch must emit the standard submission counter so execution dashboards pick up every backend");
    }

    [Fact]
    public async Task StartAsync_DoesNotRecordSubmissionMetricOnRejectedSubmission()
    {
        const string metricBackend = "honua-aws-batch-metric-rejected-test";
        var samples = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Name == ControlPlaneTelemetry.Metrics.ExecutionJobSubmitted)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? backend = null;
            foreach (var tag in tags)
            {
                if (tag.Key == ControlPlaneTelemetry.Tags.Backend)
                {
                    backend = tag.Value as string;
                    break;
                }
            }

            if (!string.Equals(backend, metricBackend, StringComparison.Ordinal))
            {
                return;
            }

            lock (samples)
            {
                samples.Add(measurement);
            }
        });
        listener.Start();

        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("invalid job definition")
            {
                StatusCode = HttpStatusCode.BadRequest
            }
        };
        var backend = CreateBackend(client);

        await backend.StartAsync(CreateJob(backend: metricBackend));

        lock (samples)
        {
            samples.Should().BeEmpty("rejected submissions must not inflate the fresh-submission counter");
        }
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
        submission.Message.Should().Contain("outcome is uncertain");
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
        submission.Message.Should().Be("AWS Batch rejected job submission.");
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
    public async Task ObserveAsync_PendingDiscoveryPastGraceFailsSubmittedJob()
    {
        // After the verification grace window an unacknowledged submit must reach a
        // terminal state so the reconciler stops polling and operators see an
        // actionable signal. No cancel signal on the record → Failed.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued) with
        {
            UpdatedAt = DateTimeOffset.UtcNow - AwsBatchComputeBackend.PendingDiscoveryGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("did not register with the provider");
    }

    [Fact]
    public async Task ObserveAsync_PendingDiscoveryPastGraceWithCancellationRequestedCancelsJob()
    {
        // Past grace plus a durable CancellationRequestedAt signal resolves to
        // Cancelled, matching Azure Batch's missing-registration rule so the
        // cancel intent is preserved across backends.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow - AwsBatchComputeBackend.PendingDiscoveryGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.Message.Should().Contain("never registered with the provider");
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
    public async Task CancelAsync_WithPendingMarker_KeepsNonTerminalDuringGraceWindow()
    {
        // An empty ListJobsByName result within the verification grace window must not
        // terminalize the record: AWS may still surface the uncertain submission, and
        // terminalizing early would orphan that real provider job. The reconciler keeps
        // calling CancelAsync while the record stays non-terminal.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        observation.ProviderOperationId.Should().Be(pendingId);
        observation.Message.Should().Contain("still being verified before cancellation completes");
    }

    [Fact]
    public async Task CancelAsync_WithPendingMarker_ReturnsCancelledWhenListJobsEmptyPastGrace()
    {
        // After the verification grace window the provider still has no record of the
        // submission: safe to terminalize the cancel. This mirrors Azure Batch's
        // missing-registration rule so operators do not see jobs hang indefinitely.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued) with
        {
            UpdatedAt = DateTimeOffset.UtcNow - AwsBatchComputeBackend.PendingDiscoveryGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.Message.Should().Contain("never registered with the provider");
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
    public async Task StartAsync_EmitsDistinctPendingMarkerPerAttempt()
    {
        // Regression for retry-aliasing: StartAsync must derive a per-attempt provider name
        // so an uncertain retry submission cannot bind via ListJobsByName to an older
        // attempt that still exists at AWS under the same operation id.
        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonServiceException("service unavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            }
        };
        var backend = CreateBackend(client);
        var jobFirst = CreateJob() with { AttemptCount = 0 };
        var jobRetry = jobFirst with { AttemptCount = 1 };

        var first = await backend.StartAsync(jobFirst);
        var retry = await backend.StartAsync(jobRetry);

        first.ProviderOperationId.Should().StartWith(AwsBatchComputeBackend.PendingSubmissionMarkerPrefix);
        retry.ProviderOperationId.Should().StartWith(AwsBatchComputeBackend.PendingSubmissionMarkerPrefix);
        first.ProviderOperationId.Should().NotBe(retry.ProviderOperationId,
            "each retry must use a unique AWS Batch job name so pending-marker discovery cannot bind to an earlier attempt");

        AwsBatchComputeBackend.TryExtractPendingJobName(first.ProviderOperationId, out var firstName).Should().BeTrue();
        AwsBatchComputeBackend.TryExtractPendingJobName(retry.ProviderOperationId, out var retryName).Should().BeTrue();
        firstName.Should().EndWith("-a0");
        retryName.Should().EndWith("-a1");
    }

    [Fact]
    public async Task ObserveAsync_RetryPendingDiscoveryOnlyResolvesCurrentAttempt()
    {
        // When a retry submit is ambiguous and the older attempt's Batch job is still
        // visible, ListJobsByName is called with the retry-specific name; the stub
        // asserts the adapter never passes the older attempt's name.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult =
            [
                new AwsBatchJobState { JobId = "aws-job-retry", Status = "RUNNABLE" }
            ]
        };
        var backend = CreateBackend(client);
        // Retry attempt (AttemptCount=1) with its uncertain-submit pending marker.
        var pendingId = AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "honua-op-1-a1";
        var job = CreateJob(providerOperationId: pendingId, status: ExecutionJobStatus.Queued) with
        {
            AttemptCount = 1
        };

        var observation = await backend.ObserveAsync(job);

        observation.ProviderOperationId.Should().Be("aws-job-retry");
        client.LastListJobsName.Should().Be("honua-op-1-a1",
            "discovery must query by the per-attempt name, not the unsuffixed operation id");
    }

    [Fact]
    public void BuildJobName_AppendsPerAttemptSuffixWithinAwsLengthBudget()
    {
        // Names must remain within AWS Batch's 128-character limit even when the
        // operation id already fills most of the budget. The retry suffix takes
        // priority so a long op-id is truncated to make room.
        var longOpId = new string('a', 200);

        var first = AwsBatchComputeBackend.BuildJobName("gp-abc_123", 0);
        var retry = AwsBatchComputeBackend.BuildJobName("gp-abc_123", 7);
        var truncated = AwsBatchComputeBackend.BuildJobName(longOpId, 99);

        first.Should().Be("gp-abc_123-a0");
        retry.Should().Be("gp-abc_123-a7");
        truncated.Length.Should().BeLessOrEqualTo(128);
        truncated.Should().EndWith("-a99");
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

    [Fact]
    public async Task ObserveAsync_RecoversProviderOwnershipWhenProvisioningWithBlankProviderId()
    {
        // Regression for post-submit write loss: Status=Provisioning with a blank
        // ProviderOperationId means the submission helper advanced the record before
        // StartAsync, then lost the second CAS write. The deterministic per-attempt
        // job name must re-bind ownership via ListJobsByName so we do not orphan a
        // real AWS Batch job.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult =
            [
                new AwsBatchJobState { JobId = "aws-job-recovered", Status = "RUNNABLE" }
            ]
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning);

        var observation = await backend.ObserveAsync(job);

        observation.ProviderOperationId.Should().Be("aws-job-recovered",
            "the orphaned provisioning record must recover its provider job via ListJobsByName");
        observation.Status.Should().Be(ExecutionJobStatus.Queued);
        client.ListJobsCallCount.Should().Be(1);
        client.LastListJobsName.Should().Be(AwsBatchComputeBackend.BuildJobName(job.OperationId, 0));
    }

    [Fact]
    public async Task ObserveAsync_RecoversProviderOwnershipWhenQueuedWithAttemptCountAboveZero()
    {
        // Regression for retry post-submit write loss: Status=Queued with AttemptCount>0
        // means a prior StartAsync succeeded but the follow-up transition lost its write.
        // The per-attempt name uses the current AttemptCount so we re-bind only to the
        // current retry, not an earlier AWS Batch job under the same operation id.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult =
            [
                new AwsBatchJobState { JobId = "aws-job-retry", Status = "RUNNABLE" }
            ]
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Queued) with
        {
            AttemptCount = 2
        };

        var observation = await backend.ObserveAsync(job);

        observation.ProviderOperationId.Should().Be("aws-job-retry");
        client.LastListJobsName.Should().EndWith("-a2",
            "recovery must query the current attempt's job name, not an earlier attempt");
    }

    [Fact]
    public async Task ObserveAsync_OrphanRecoveryFailsAfterGraceWindowWhenStillUnseen()
    {
        // If the orphan record sits past the grace window with no AWS match, the
        // pending-discovery path terminalizes to Failed so operators see a signal.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning) with
        {
            UpdatedAt = DateTimeOffset.UtcNow - AwsBatchComputeBackend.PendingDiscoveryGracePeriod - TimeSpan.FromSeconds(5)
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("did not register with the provider");
    }

    [Fact]
    public async Task CancelAsync_RecoversProviderOwnershipAndTerminatesWhenProvisioningWithBlankProviderId()
    {
        // Regression for post-submit write loss on cancel: a record that crossed the
        // remote-start boundary without a persisted provider id must NOT short-circuit
        // to "cancelled before submission"; that would orphan an accepted AWS Batch
        // job. Instead, rediscover by per-attempt name and route through the normal
        // describe/cancel path.
        var client = new StubAwsBatchJobClient();
        client.NextListJobsResult =
        [
            new AwsBatchJobState { JobId = "aws-job-recovered", Status = "RUNNABLE" }
        ];
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-recovered",
            Status = "RUNNABLE"
        });
        client.DescribeResults.Enqueue(new AwsBatchJobState
        {
            JobId = "aws-job-recovered",
            Status = "FAILED",
            StatusReason = AwsBatchStateMapper.CancelReason
        });
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.ProviderOperationId.Should().Be("aws-job-recovered");
        client.LastListJobsName.Should().Be(AwsBatchComputeBackend.BuildJobName(job.OperationId, 0));
        client.CancelCallCount.Should().Be(1,
            "orphan recovery must still issue a provider-level cancel against the recovered job id");
    }

    [Fact]
    public async Task CancelAsync_OrphanRecoveryStaysNonterminalWithinGraceWindow()
    {
        // Mirrors the pending-marker cancel rule: if AWS has no record yet and we are
        // still within the verification grace window, keep the durable record active
        // so the reconciler can keep retrying discovery.
        var client = new StubAwsBatchJobClient
        {
            NextListJobsResult = Array.Empty<AwsBatchJobState>()
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Provisioning,
            "within the grace window the orphaned cancel must stay non-terminal pending discovery");
        observation.Message.Should().Contain("still being verified before cancellation completes");
    }

    [Fact]
    public async Task StartAsync_TreatsClientSideIdentityFailureAsUncertain()
    {
        // Regression for client-side AWS SDK failures: the credential-resolution path
        // throws AmazonClientException directly (not AmazonServiceException). Those
        // must keep the record nonterminal via the uncertain-submit marker, not escape
        // into the reconciler's generic catch.
        var client = new StubAwsBatchJobClient
        {
            SubmitException = new AmazonClientException("Unable to resolve identity: token expired")
        };
        var backend = CreateBackend(client);

        var submission = await backend.StartAsync(CreateJob());

        submission.Status.Should().Be(ExecutionJobStatus.Queued);
        submission.ProviderOperationId.Should().StartWith(AwsBatchComputeBackend.PendingSubmissionMarkerPrefix);
        submission.Message.Should().Contain("outcome is uncertain");
    }

    [Fact]
    public async Task ObserveAsync_PreservesStatusOnClientSideIdentityFailure()
    {
        // Client-side failures (AmazonClientException) during observe must stay inside
        // the adapter so the reconciler's generic catch does not stamp the durable
        // record terminal Failed while the AWS Batch job could still be running.
        var client = new StubAwsBatchJobClient
        {
            DescribeException = new AmazonClientException("identity resolution failed: missing profile")
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("observation failed");
    }

    [Fact]
    public async Task CancelAsync_PreservesStatusOnClientSideIdentityFailure()
    {
        // Client-side failures during cancel must preserve durable state. Without this,
        // a transient credential blip would surface as an unhandled 500 to the caller.
        var client = new StubAwsBatchJobClient
        {
            DescribeException = new AmazonClientException("identity resolution failed: missing profile")
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("cancellation failed");
    }

    [Fact]
    public async Task ObserveAsync_OrphanRecoveryPreservesStatusOnClientSideFailure()
    {
        // Client-side identity/transport failure hit while rediscovering an orphaned
        // record must leave the durable record non-terminal so reconciliation can
        // retry on the next cycle.
        var client = new StubAwsBatchJobClient
        {
            ListJobsException = new AmazonClientException("credential provider chain returned no credentials")
        };
        var backend = CreateBackend(client);
        var job = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Provisioning);
        observation.Message.Should().Contain("discovery failed");
    }

    [Fact]
    public void TryDeriveOrphanedSubmissionName_OnlyTriggersAfterRemoteStartBoundary()
    {
        // Initial pre-submission state (Queued + AttemptCount=0 + blank providerId) must
        // stay on the normal StartAsync path.
        var freshJob = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Queued);
        AwsBatchComputeBackend.TryDeriveOrphanedSubmissionName(freshJob, out _).Should().BeFalse();

        // A record with a real providerId is not orphaned.
        var submittedJob = CreateJob(providerOperationId: "aws-job-1", status: ExecutionJobStatus.Queued);
        AwsBatchComputeBackend.TryDeriveOrphanedSubmissionName(submittedJob, out _).Should().BeFalse();

        // Boundary-crossed records with blank providerId trigger recovery.
        var provisioningJob = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Provisioning);
        AwsBatchComputeBackend.TryDeriveOrphanedSubmissionName(provisioningJob, out var provisioningName).Should().BeTrue();
        provisioningName.Should().EndWith("-a0");

        var retryJob = CreateJob(providerOperationId: null, status: ExecutionJobStatus.Queued) with { AttemptCount = 3 };
        AwsBatchComputeBackend.TryDeriveOrphanedSubmissionName(retryJob, out var retryName).Should().BeTrue();
        retryName.Should().EndWith("-a3");
    }

    private static AwsBatchComputeBackend CreateBackend(IAwsBatchJobClient client)
        => new(client, NullLogger<AwsBatchComputeBackend>.Instance);

    internal static ExecutionJobRecord CreateJob(
        IReadOnlyDictionary<string, string>? parameters = null,
        string? providerOperationId = null,
        ExecutionJobStatus status = ExecutionJobStatus.Queued,
        string backend = "honua-aws-batch")
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
                Backend = backend,
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

    public Exception? SubmitException { get; set; }

    public Exception? DescribeException { get; set; }

    public Exception? ListJobsException { get; set; }

    public Exception? CancelException { get; set; }

    public Exception? TerminateException { get; set; }

    public AwsBatchJobSubmission? LastSubmission { get; private set; }

    public string? LastRegion { get; private set; }

    public string? LastServiceUrl { get; private set; }

    public string? LastListJobsQueue { get; private set; }

    public string? LastListJobsName { get; private set; }

    public int CancelCallCount { get; private set; }

    public int TerminateCallCount { get; private set; }

    public int DescribeCallCount { get; private set; }

    public int ListJobsCallCount { get; private set; }

    public Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        LastSubmission = submission;
        LastRegion = region;
        LastServiceUrl = serviceUrl;
        if (SubmitException != null)
        {
            throw SubmitException;
        }

        return Task.FromResult(NextSubmitResult);
    }

    public Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        DescribeCallCount++;
        LastRegion = region;
        LastServiceUrl = serviceUrl;
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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ListJobsCallCount++;
        LastListJobsQueue = jobQueue;
        LastListJobsName = jobName;
        LastRegion = region;
        LastServiceUrl = serviceUrl;
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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        CancelCallCount++;
        LastServiceUrl = serviceUrl;
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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        TerminateCallCount++;
        LastServiceUrl = serviceUrl;
        if (TerminateException != null)
        {
            throw TerminateException;
        }

        return Task.CompletedTask;
    }
}
