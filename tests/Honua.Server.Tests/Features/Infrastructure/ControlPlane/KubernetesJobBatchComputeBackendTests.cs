// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesJobBatchComputeBackendTests
{
    [Fact]
    public void BackendIdentity_MatchesContract()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        backend.BackendName.Should().Be(KubernetesJobBatchComputeBackend.BackendId);
        backend.BackendName.Should().Be("honua-kubernetes-job");
        backend.TargetKind.Should().Be(BatchComputeTargetKind.KubernetesJob);
    }

    [Fact]
    public async Task GetCapabilities_ReturnsExpectedFlags()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsCancellation.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRetry.Should().BeTrue();
        capabilities.SupportsArtifactStaging.Should().BeTrue();
        capabilities.SupportsLogStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithNoImage_ReturnsFailed()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        var backend = CreateBackend(client);
        var job = CreateJob("job-no-image");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("container image");
        await client.DidNotReceiveWithAnyArgs().CreateJobAsync(default!);
    }

    [Fact]
    public async Task StartAsync_SuccessfulCreate_ReturnsProvisioning()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.CreateJobAsync(Arg.Any<KubernetesJobManifest>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobCreateResult
            {
                StatusCode = HttpStatusCode.Created,
                Snapshot = new KubernetesJobStatusSnapshot { Uid = "job-uid-1" }
            });
        var backend = CreateBackend(client);
        var job = CreateJob("job-create", image: "honua/worker:1.0.0");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Provisioning);
        result.ProviderOperationId.Should().Be("job-uid-1");
        result.Message.Should().Contain("honua-job-create");
        await client.Received(1).CreateJobAsync(
            Arg.Is<KubernetesJobManifest>(m =>
                m.Image == "honua/worker:1.0.0" &&
                m.Name == "honua-job-create" &&
                m.Namespace == "default"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_Conflict_IsIdempotent()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.CreateJobAsync(Arg.Any<KubernetesJobManifest>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobCreateResult { StatusCode = HttpStatusCode.Conflict });
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult
            {
                StatusCode = HttpStatusCode.OK,
                Snapshot = new KubernetesJobStatusSnapshot { Uid = "uid-existing", Active = 1 }
            });
        var backend = CreateBackend(client);
        var job = CreateJob("job-conflict", image: "honua/worker:1.0.0");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Running);
        result.ProviderOperationId.Should().Be("uid-existing");
        result.Message.Should().Contain("idempotent");
    }

    [Fact]
    public async Task StartAsync_HttpFailure_ReturnsFailed()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.CreateJobAsync(Arg.Any<KubernetesJobManifest>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("cluster unreachable"));
        var backend = CreateBackend(client);
        var job = CreateJob("job-failed", image: "honua/worker:1.0.0");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("cluster unreachable");
    }

    [Fact]
    public async Task StartAsync_InvalidOperation_ReturnsFailed()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.CreateJobAsync(Arg.Any<KubernetesJobManifest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Kubernetes execution backend is configured but no API server endpoint is available."));
        var backend = CreateBackend(client);
        var job = CreateJob("job-config-err", image: "honua/worker:1.0.0");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("no API server endpoint");
    }

    [Fact]
    public async Task StartAsync_UriFormat_ReturnsFailed()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.CreateJobAsync(Arg.Any<KubernetesJobManifest>(), Arg.Any<CancellationToken>())
            .Throws(new UriFormatException("Invalid URI: The format of the URI could not be determined."));
        var backend = CreateBackend(client);
        var job = CreateJob("job-bad-uri", image: "honua/worker:1.0.0");

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("Invalid URI");
    }

    [Fact]
    public async Task ObserveAsync_ConfigError_PreservesCurrentStatus()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("ApiServerUrl is not configured."));
        var backend = CreateBackend(client);
        var job = CreateJob("job-observe-config", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);
        job = job with
        {
            Spec = job.Spec with
            {
                Parameters = new Dictionary<string, string>(job.Spec.Parameters)
                {
                    [KubernetesJobParameterKeys.Namespace] = "honua"
                }
            }
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("ApiServerUrl");
    }

    [Fact]
    public async Task CancelAsync_IOError_PreservesCurrentStatus()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.DeleteJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new IOException("bearer token file unreadable"));
        var backend = CreateBackend(client);
        var job = CreateJob("job-cancel-io", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("bearer token file");
    }

    [Fact]
    public async Task ObserveAsync_RunningJob_ReturnsRunning()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync("honua", "honua-job-observe", Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult
            {
                StatusCode = HttpStatusCode.OK,
                Snapshot = new KubernetesJobStatusSnapshot { Uid = "uid-1", Active = 1 }
            });
        var backend = CreateBackend(client);
        var job = CreateJob("job-observe", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Provisioning);
        job = job with
        {
            Spec = job.Spec with
            {
                Parameters = new Dictionary<string, string>(job.Spec.Parameters)
                {
                    [KubernetesJobParameterKeys.Namespace] = "honua"
                }
            }
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.ProviderOperationId.Should().Be("uid-1");
        observation.Message.Should().Contain("1 active");
    }

    [Fact]
    public async Task ObserveAsync_FailedJob_AnnotatesContainerTermination()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult
            {
                StatusCode = HttpStatusCode.OK,
                Snapshot = new KubernetesJobStatusSnapshot
                {
                    Uid = "uid-failed",
                    Failed = 1,
                    FailedCondition = true,
                    TerminalReason = "BackoffLimitExceeded"
                }
            });
        client.ListPodsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<KubernetesPodStatusSnapshot>
            {
                new()
                {
                    Name = "pod-1",
                    Phase = "Failed",
                    ContainerTerminationReason = "OOMKilled",
                    ContainerTerminationMessage = "worker exited",
                    ContainerExitCode = 137
                }
            });

        var backend = CreateBackend(client);
        var job = CreateJob("job-fail", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("worker exited");
    }

    [Fact]
    public async Task ObserveAsync_JobDisappeared_TreatsAsFailedWhenNotTerminal()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult { StatusCode = HttpStatusCode.NotFound });

        var backend = CreateBackend(client);
        var job = CreateJob("job-ghost", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("no longer present");
    }

    [Fact]
    public async Task ObserveAsync_JobDisappeared_PreservesSucceededAfterTtlCleanup()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult { StatusCode = HttpStatusCode.NotFound });

        var backend = CreateBackend(client);
        var job = CreateJob("job-ttl", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Succeeded);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Succeeded);
        observation.Message.Should().Contain("cleaned up");
    }

    [Fact]
    public async Task ObserveAsync_JobDisappeared_PreservesCancelledTerminalState()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult { StatusCode = HttpStatusCode.NotFound });

        var backend = CreateBackend(client);
        var job = CreateJob("job-cancelled", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Cancelled);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [Fact]
    public async Task ObserveAsync_SucceededJob_PromotesPercentToHundred()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult
            {
                StatusCode = HttpStatusCode.OK,
                Snapshot = new KubernetesJobStatusSnapshot
                {
                    Uid = "uid-done",
                    Succeeded = 1,
                    CompleteCondition = true
                }
            });

        var backend = CreateBackend(client);
        var job = CreateJob("job-success", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Succeeded);
        observation.PercentComplete.Should().Be(100d);
    }

    [Fact]
    public async Task CancelAsync_MissingJob_IsTreatedAsCancelled()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.DeleteJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobDeleteResult { StatusCode = HttpStatusCode.NotFound });

        var backend = CreateBackend(client);
        var job = CreateJob("job-cancel", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.Message.Should().Contain("already absent");
    }

    [Fact]
    public async Task CancelAsync_DeleteSucceeds_ReturnsCancelled()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.DeleteJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobDeleteResult { StatusCode = HttpStatusCode.OK });

        var backend = CreateBackend(client);
        var job = CreateJob("job-cancel-ok", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        observation.Message.Should().Contain("cascade deletion");
    }

    [Fact]
    public async Task CancelAsync_HttpFailure_PreservesCurrentStatus()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.DeleteJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("boom"));

        var backend = CreateBackend(client);
        var job = CreateJob("job-cancel-err", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.Message.Should().Contain("boom");
    }

    [Fact]
    public void BuildManifest_ProjectsParametersAndLabels()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "system",
            DefaultImage = "honua/worker:ignored",
            DefaultImagePullPolicy = "IfNotPresent",
            DefaultCpuRequest = "200m",
            DefaultMemoryRequest = "512Mi",
            DefaultActiveDeadlineSeconds = 600,
            DefaultTtlSecondsAfterFinished = 1800
        });

        var job = CreateJob("job-manifest", image: "honua/gdal:latest") with
        {
            Audit = new OperationAuditInfo { CorrelationId = "corr-1", RequestedBy = "operator" },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = KubernetesJobBatchComputeBackend.BackendId,
                WorkloadId = "buffer-5km",
                WorkloadName = "Buffer Analysis",
                Artifact = "honua/gdal:latest",
                Parameters = new Dictionary<string, string>
                {
                    [KubernetesJobParameterKeys.Namespace] = "geoprocessing",
                    [KubernetesJobParameterKeys.CpuLimit] = "2",
                    [KubernetesJobParameterKeys.MemoryLimit] = "8Gi",
                    [KubernetesJobParameterKeys.NodeSelector] = "pool=heavy,disk=ssd",
                    [KubernetesJobParameterKeys.ImagePullSecrets] = "regcreds",
                    [KubernetesJobParameterKeys.ServiceAccount] = "jobs-runner",
                    [KubernetesJobParameterKeys.ImagePullPolicy] = "Always",
                    [KubernetesJobParameterKeys.ActiveDeadlineSeconds] = "3600",
                    [KubernetesJobParameterKeys.TtlSecondsAfterFinished] = "300",
                    ["k8s.env.HONUA_WORKLOAD_ID"] = "buffer-5km"
                }
            }
        };

        var manifest = backend.BuildManifest(job, "honua/gdal:latest");

        manifest.Namespace.Should().Be("geoprocessing");
        manifest.Name.Should().Be("honua-job-manifest");
        manifest.Image.Should().Be("honua/gdal:latest");
        manifest.CpuRequest.Should().Be("200m");
        manifest.CpuLimit.Should().Be("2");
        manifest.MemoryLimit.Should().Be("8Gi");
        manifest.NodeSelector.Should().ContainKey("pool").WhoseValue.Should().Be("heavy");
        manifest.NodeSelector.Should().ContainKey("disk").WhoseValue.Should().Be("ssd");
        manifest.ImagePullSecrets.Should().ContainSingle().Which.Should().Be("regcreds");
        manifest.ServiceAccount.Should().Be("jobs-runner");
        manifest.ImagePullPolicy.Should().Be("Always");
        manifest.ActiveDeadlineSeconds.Should().Be(3600);
        manifest.TtlSecondsAfterFinished.Should().Be(300);
        manifest.EnvironmentVariables.Should().ContainKey("HONUA_WORKLOAD_ID").WhoseValue.Should().Be("buffer-5km");
        manifest.Labels.Should().ContainKey("honua.io/operation-id");
        manifest.Labels["honua.io/operation-id"].Should().Be("job-manifest");
        manifest.Labels["app.kubernetes.io/managed-by"].Should().Be("honua-controlplane");
        manifest.Annotations.Should().ContainKey("honua.io/correlation-id");
    }

    [Fact]
    public void BuildManifest_LongOperationId_ProducesValidKubernetesName()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var longId = new string('a', 120);
        var job = CreateJob(longId, image: "honua/worker:1.0.0");

        var manifest = backend.BuildManifest(job, "honua/worker:1.0.0");

        manifest.Name.Length.Should().BeLessThanOrEqualTo(63);
        manifest.Name.Should().StartWith("honua-");
    }

    [Fact]
    public void BuildManifest_TimeoutPolicyDefault_TranslatesToActiveDeadlineSeconds()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>());
        var job = CreateJob("job-timeout", image: "honua/worker:1.0.0") with
        {
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromSeconds(7200) }
        };

        var manifest = backend.BuildManifest(job, "honua/worker:1.0.0");

        manifest.ActiveDeadlineSeconds.Should().Be(7200);
    }

    [Fact]
    public void BuildManifest_ExplicitTtlBelowMinimum_IsClampedToSafeFloor()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "default",
            DefaultTtlSecondsAfterFinished = 3600
        });
        var job = CreateJob("job-ttl-clamp", image: "honua/worker:1.0.0") with
        {
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = KubernetesJobBatchComputeBackend.BackendId,
                WorkloadId = "wl",
                WorkloadName = "workload",
                Artifact = "honua/worker:1.0.0",
                Parameters = new Dictionary<string, string>
                {
                    [KubernetesJobParameterKeys.TtlSecondsAfterFinished] = "0"
                }
            }
        };

        var manifest = backend.BuildManifest(job, "honua/worker:1.0.0");

        manifest.TtlSecondsAfterFinished.Should().Be(KubernetesJobBatchComputeBackend.MinimumTtlSecondsAfterFinished);
    }

    [Fact]
    public void BuildManifest_DefaultTtlBelowMinimum_IsClampedToSafeFloor()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "default",
            DefaultTtlSecondsAfterFinished = 5
        });
        var job = CreateJob("job-ttl-default", image: "honua/worker:1.0.0");

        var manifest = backend.BuildManifest(job, "honua/worker:1.0.0");

        manifest.TtlSecondsAfterFinished.Should().Be(KubernetesJobBatchComputeBackend.MinimumTtlSecondsAfterFinished);
    }

    [Fact]
    public void BuildManifest_NullDefaultTtl_RemainsNull()
    {
        var backend = CreateBackend(Substitute.For<IKubernetesJobClient>(), new KubernetesExecutionOptions
        {
            DefaultNamespace = "default",
            DefaultTtlSecondsAfterFinished = null
        });
        var job = CreateJob("job-ttl-null", image: "honua/worker:1.0.0");

        var manifest = backend.BuildManifest(job, "honua/worker:1.0.0");

        manifest.TtlSecondsAfterFinished.Should().BeNull();
    }

    [Fact]
    public async Task ObserveAsync_NoNamespaceConfigured_FallsBackToDefault()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.GetJobAsync("default", "honua-job-observe-default", Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobFetchResult
            {
                StatusCode = HttpStatusCode.OK,
                Snapshot = new KubernetesJobStatusSnapshot { Uid = "uid-default", Active = 1 }
            });
        var backend = CreateBackend(client, new KubernetesExecutionOptions { DefaultNamespace = null });
        var job = CreateJob("job-observe-default", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Provisioning);

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Running);
        observation.ProviderOperationId.Should().Be("uid-default");
        await client.Received(1).GetJobAsync("default", "honua-job-observe-default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_NoNamespaceConfigured_FallsBackToDefault()
    {
        var client = Substitute.For<IKubernetesJobClient>();
        client.DeleteJobAsync("default", "honua-job-cancel-default", Arg.Any<CancellationToken>())
            .Returns(new KubernetesJobDeleteResult { StatusCode = HttpStatusCode.OK });
        var backend = CreateBackend(client, new KubernetesExecutionOptions { DefaultNamespace = null });
        var job = CreateJob("job-cancel-default", image: "honua/worker:1.0.0", status: ExecutionJobStatus.Running);

        var observation = await backend.CancelAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Cancelled);
        await client.Received(1).DeleteJobAsync("default", "honua-job-cancel-default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MapStatus_FailedConditionWins()
    {
        var snapshot = new KubernetesJobStatusSnapshot { FailedCondition = true };
        var status = KubernetesJobBatchComputeBackend.MapStatus(snapshot, ExecutionJobStatus.Running);
        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [Fact]
    public void MapStatus_CompleteConditionYieldsSucceeded()
    {
        var snapshot = new KubernetesJobStatusSnapshot { CompleteCondition = true };
        var status = KubernetesJobBatchComputeBackend.MapStatus(snapshot, ExecutionJobStatus.Running);
        status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [Fact]
    public void MapStatus_ActivePodsYieldRunning()
    {
        var snapshot = new KubernetesJobStatusSnapshot { Active = 2 };
        var status = KubernetesJobBatchComputeBackend.MapStatus(snapshot, ExecutionJobStatus.Provisioning);
        status.Should().Be(ExecutionJobStatus.Running);
    }

    [Fact]
    public void MapStatus_NoActivityYieldsProvisioningUnlessAlreadyRunning()
    {
        var snapshot = new KubernetesJobStatusSnapshot();
        KubernetesJobBatchComputeBackend.MapStatus(snapshot, ExecutionJobStatus.Queued)
            .Should().Be(ExecutionJobStatus.Provisioning);
        KubernetesJobBatchComputeBackend.MapStatus(snapshot, ExecutionJobStatus.Running)
            .Should().Be(ExecutionJobStatus.Running);
    }

    [Fact]
    public void SerializeJobManifest_ProducesExpectedShape()
    {
        var manifest = new KubernetesJobManifest
        {
            Namespace = "geoprocessing",
            Name = "honua-buffer",
            Image = "honua/gdal:latest",
            Labels = new Dictionary<string, string> { ["k"] = "v" },
            Annotations = new Dictionary<string, string> { ["honua.io/note"] = "hello" },
            CpuRequest = "250m",
            MemoryLimit = "2Gi",
            NodeSelector = new Dictionary<string, string> { ["pool"] = "heavy" },
            ImagePullSecrets = new[] { "regcreds" },
            ServiceAccount = "jobs-runner",
            ImagePullPolicy = "Always",
            TtlSecondsAfterFinished = 600,
            ActiveDeadlineSeconds = 3600,
            EnvironmentVariables = new Dictionary<string, string> { ["HONUA"] = "1" }
        };

        var bytes = KubernetesJobManifestSerializer.SerializeJobManifest(manifest);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        root.GetProperty("apiVersion").GetString().Should().Be("batch/v1");
        root.GetProperty("kind").GetString().Should().Be("Job");
        root.GetProperty("metadata").GetProperty("name").GetString().Should().Be("honua-buffer");
        root.GetProperty("spec").GetProperty("backoffLimit").GetInt32().Should().Be(0);
        root.GetProperty("spec").GetProperty("ttlSecondsAfterFinished").GetInt32().Should().Be(600);
        root.GetProperty("spec").GetProperty("activeDeadlineSeconds").GetInt32().Should().Be(3600);

        var podSpec = root.GetProperty("spec").GetProperty("template").GetProperty("spec");
        podSpec.GetProperty("restartPolicy").GetString().Should().Be("Never");
        podSpec.GetProperty("serviceAccountName").GetString().Should().Be("jobs-runner");

        var container = podSpec.GetProperty("containers")[0];
        container.GetProperty("image").GetString().Should().Be("honua/gdal:latest");
        container.GetProperty("imagePullPolicy").GetString().Should().Be("Always");
        container.GetProperty("env")[0].GetProperty("name").GetString().Should().Be("HONUA");
        container.GetProperty("resources").GetProperty("requests").GetProperty("cpu").GetString().Should().Be("250m");
        container.GetProperty("resources").GetProperty("limits").GetProperty("memory").GetString().Should().Be("2Gi");
    }

    [Fact]
    public void ParseJobStatus_ReadsActiveAndConditions()
    {
        var json = /*lang=json,strict*/ """
            {
              "metadata": { "uid": "uid-1", "namespace": "honua", "name": "honua-job" },
              "status": {
                "active": 1,
                "succeeded": 0,
                "failed": 0,
                "conditions": [
                  { "type": "Complete", "status": "False" }
                ]
              }
            }
        """;

        using var document = JsonDocument.Parse(json);
        var snapshot = KubernetesJobManifestSerializer.ParseJobStatus(document.RootElement);

        snapshot.Uid.Should().Be("uid-1");
        snapshot.Active.Should().Be(1);
        snapshot.CompleteCondition.Should().BeFalse();
        snapshot.FailedCondition.Should().BeFalse();
    }

    [Fact]
    public void ParseJobStatus_ExtractsFailedCondition()
    {
        var json = /*lang=json,strict*/ """
            {
              "metadata": { "uid": "uid-2", "namespace": "honua", "name": "honua-job" },
              "status": {
                "failed": 1,
                "conditions": [
                  { "type": "Failed", "status": "True", "reason": "BackoffLimitExceeded", "message": "Pod failure" }
                ]
              }
            }
        """;
        using var document = JsonDocument.Parse(json);
        var snapshot = KubernetesJobManifestSerializer.ParseJobStatus(document.RootElement);

        snapshot.FailedCondition.Should().BeTrue();
        snapshot.TerminalReason.Should().Be("BackoffLimitExceeded");
        snapshot.TerminalMessage.Should().Be("Pod failure");
    }

    private static KubernetesJobBatchComputeBackend CreateBackend(
        IKubernetesJobClient client,
        KubernetesExecutionOptions? options = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<KubernetesExecutionOptions>>();
        monitor.CurrentValue.Returns(options ?? new KubernetesExecutionOptions
        {
            DefaultNamespace = "default"
        });
        return new KubernetesJobBatchComputeBackend(
            client,
            monitor,
            NullLogger<KubernetesJobBatchComputeBackend>.Instance);
    }

    private static ExecutionJobRecord CreateJob(
        string operationId,
        string? image = null,
        ExecutionJobStatus status = ExecutionJobStatus.Queued)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = KubernetesJobBatchComputeBackend.BackendId,
            WorkloadId = "wl",
            WorkloadName = "workload",
            Artifact = image,
            Parameters = parameters
        };

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = spec
        };
    }
}
