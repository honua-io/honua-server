// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Free-tier scenario (#2163): proves the per-target Job submission → run → terminal-result
/// lifecycle for <c>KubernetesJobBatchComputeBackend</c> against a real kind cluster. Honua is
/// serverless at every level with no standing compute, and each GP job is a unique per-job
/// submission, so this validates the per-submission lifecycle rather than a fixed cluster.
///
/// kind/k3d is free real Kubernetes-in-Docker, so this is the default CI lane. When no kind
/// cluster is wired (no <c>HONUA_TEST_K8S_*</c> env vars), the tests skip rather than fail so
/// the project still runs on boxes without a cluster.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]
[Trait(CloudIntegrationTraits.Tier, CloudIntegrationTraits.Free)]
public sealed class KubernetesJobLifecycleCloudIntegrationTests : IClassFixture<KindClusterFixture>
{
    // A tiny worker image whose default entrypoint runs to completion and exits 0 — stands in
    // for a GP worker. The backend's BuildManifest does not set a container command/args, so
    // the image MUST self-terminate via its own entrypoint. The CI workflow builds this image
    // (entrypoint = /bin/true) and loads it into the kind node so it resolves with an
    // image-pull-policy of Never. Overridable via env so a local run can point at a different
    // self-terminating image already present on the node.
    private static readonly string SucceedingImage =
        Environment.GetEnvironmentVariable("HONUA_TEST_K8S_JOB_IMAGE") ?? "honua-ci/job-succeed:latest";

    private readonly KindClusterFixture _kind;

    public KubernetesJobLifecycleCloudIntegrationTests(KindClusterFixture kind)
    {
        _kind = kind;
    }

    [SkippableFact]
    public async Task SubmitJob_RunsToCompletion_ReportsSucceeded()
    {
        Skip.IfNot(_kind.Available, "kind cluster not configured (HONUA_TEST_K8S_* env vars absent).");

        var (backend, provider) = KubernetesBackendFactory.Create(
            _kind.ApiServerUrl!,
            _kind.BearerToken!,
            _kind.CaBundlePath!,
            _kind.Namespace,
            defaultImage: SucceedingImage);

        var operationId = $"ci-k8s-{Guid.NewGuid():N}".Substring(0, 24);
        await using var _ = provider;

        var job = CreateJob(operationId, image: SucceedingImage);

        try
        {
            // 1. Submit: the backend must translate the record into a batch/v1 Job and create it.
            var submission = await backend.StartAsync(job);
            submission.Status.Should().BeOneOf(
                ExecutionJobStatus.Provisioning,
                ExecutionJobStatus.Queued);
            submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();

            // Pin the namespace the backend resolved so later observe calls target the same Job.
            var observedJob = MergeResolvedParameters(job, submission);

            // 2. Observe until terminal: poll the real cluster through the backend's observe path.
            var terminal = await PollUntilTerminalAsync(backend, observedJob, TimeSpan.FromMinutes(4));
            terminal.Status.Should().Be(
                ExecutionJobStatus.Succeeded,
                "the busybox job exits 0 and the backend maps a completed Job to Succeeded");
        }
        finally
        {
            // 3. Clean up: best-effort cancel/delete so the namespace does not accumulate Jobs.
            try
            {
                await backend.CancelAsync(job);
            }
            catch
            {
                // Cleanup is best-effort; the cluster is ephemeral CI infrastructure.
            }
        }
    }

    private static async Task<BatchComputeObservation> PollUntilTerminalAsync(
        KubernetesJobBatchComputeBackend backend,
        ExecutionJobRecord job,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        BatchComputeObservation last = new() { Status = ExecutionJobStatus.Queued };

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await backend.ObserveAsync(job);
            if (last.Status is ExecutionJobStatus.Succeeded
                or ExecutionJobStatus.Failed
                or ExecutionJobStatus.Cancelled)
            {
                return last;
            }

            // Carry the provider id forward so observe keeps resolving the same Job.
            if (!string.IsNullOrWhiteSpace(last.ProviderOperationId))
            {
                job = job with { ProviderOperationId = last.ProviderOperationId };
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        throw new TimeoutException(
            $"Kubernetes Job did not reach a terminal state within {timeout}. Last status: {last.Status}.");
    }

    private static ExecutionJobRecord MergeResolvedParameters(
        ExecutionJobRecord job,
        BatchComputeSubmissionResult submission)
    {
        var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal);
        if (submission.ResolvedParameters is { Count: > 0 })
        {
            foreach (var (key, value) in submission.ResolvedParameters)
            {
                parameters[key] = value;
            }
        }

        return job with
        {
            ProviderOperationId = submission.ProviderOperationId,
            Spec = job.Spec with { Parameters = parameters }
        };
    }

    private static ExecutionJobRecord CreateJob(string operationId, string image)
    {
        var spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = KubernetesJobBatchComputeBackend.BackendId,
            WorkloadId = "ci-harness-workload",
            WorkloadName = "cloud-integration-gp",
            Artifact = image,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesJobParameterKeys.Image] = image,
                // The image is preloaded into the kind node by the workflow, so never attempt a
                // registry pull (which would fail for the local-only honua-ci/job-succeed tag).
                [KubernetesJobParameterKeys.ImagePullPolicy] = "Never"
            }
        };

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = spec
        };
    }
}
