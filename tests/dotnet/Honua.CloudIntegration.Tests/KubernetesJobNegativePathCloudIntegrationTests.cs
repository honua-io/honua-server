// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Free-tier emulated scenarios (#2166) that broaden the cloud-integration matrix beyond the
/// happy-path <see cref="KubernetesJobLifecycleCloudIntegrationTests"/> into the failure and
/// cancellation paths of <c>KubernetesJobBatchComputeBackend</c> against a real kind cluster.
/// These are the GP-job analogue of the deploy-safety "failure-of-failure" coverage: a worker
/// that exits non-zero must surface terminally as <see cref="ExecutionJobStatus.Failed"/> (the
/// backend pins <c>backoffLimit: 0</c> so a failed Pod is not silently retried in-cluster), and a
/// cancellation of a still-running Job must surface <see cref="ExecutionJobStatus.Cancelled"/>.
///
/// Gated identically to the lifecycle test: when no kind cluster is wired (no
/// <c>HONUA_TEST_K8S_*</c> env vars) every test skips rather than fails, so the project still
/// builds and runs (skipping) on boxes without a cluster.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]
public sealed class KubernetesJobNegativePathCloudIntegrationTests : IClassFixture<KindClusterFixture>
{
    // A worker image whose entrypoint exits non-zero (/bin/false) — stands in for a GP worker
    // that fails. The CI workflow builds this image and loads it into the kind node so it
    // resolves with ImagePullPolicy=Never. Overridable so a local run can point at a different
    // self-terminating, non-zero-exit image already present on the node.
    private static readonly string FailingImage =
        Environment.GetEnvironmentVariable("HONUA_TEST_K8S_FAIL_IMAGE") ?? "honua-ci/job-fail:latest";

    // A long-running worker image (sleep) — stands in for a GP worker that is still running when
    // a cancellation arrives. Loaded into the kind node by the workflow with ImagePullPolicy=Never.
    private static readonly string SleepingImage =
        Environment.GetEnvironmentVariable("HONUA_TEST_K8S_SLEEP_IMAGE") ?? "honua-ci/job-sleep:latest";

    private readonly KindClusterFixture _kind;

    public KubernetesJobNegativePathCloudIntegrationTests(KindClusterFixture kind)
    {
        _kind = kind;
    }

    [SkippableFact]
    public async Task SubmitJob_ContainerExitsNonZero_ReportsFailed()
    {
        Skip.IfNot(_kind.Available, "kind cluster not configured (HONUA_TEST_K8S_* env vars absent).");

        var (backend, provider) = KubernetesBackendFactory.Create(
            _kind.ApiServerUrl!,
            _kind.BearerToken!,
            _kind.CaBundlePath!,
            _kind.Namespace,
            defaultImage: FailingImage);
        await using var _ = provider;

        var operationId = $"ci-k8s-fail-{Guid.NewGuid():N}"[..24];
        var job = CreateJob(operationId, FailingImage);

        try
        {
            // 1. Submit: the failing worker Job must be created like any other.
            var submission = await backend.StartAsync(job);
            submission.Status.Should().BeOneOf(ExecutionJobStatus.Provisioning, ExecutionJobStatus.Queued);
            submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();

            var observedJob = MergeResolvedParameters(job, submission);

            // 2. Observe until terminal: the Pod exits 1, the Job reaches its backoffLimit (0),
            //    and the backend must map the failed Job to Failed — not retry it in-cluster.
            var terminal = await PollUntilTerminalAsync(backend, observedJob, TimeSpan.FromMinutes(4));
            terminal.Status.Should().Be(
                ExecutionJobStatus.Failed,
                "a worker that exits non-zero must surface terminally as Failed, not be silently retried");
        }
        finally
        {
            await TryCancelAsync(backend, job);
        }
    }

    [SkippableFact]
    public async Task CancelJob_WhileRunning_ReportsCancelled()
    {
        Skip.IfNot(_kind.Available, "kind cluster not configured (HONUA_TEST_K8S_* env vars absent).");

        var (backend, provider) = KubernetesBackendFactory.Create(
            _kind.ApiServerUrl!,
            _kind.BearerToken!,
            _kind.CaBundlePath!,
            _kind.Namespace,
            defaultImage: SleepingImage);
        await using var _ = provider;

        var operationId = $"ci-k8s-cancel-{Guid.NewGuid():N}"[..24];
        // An activeDeadlineSeconds safety net guarantees a leaked sleep Job self-terminates even
        // if cancellation cleanup is interrupted; the test cancels well before this fires.
        var job = CreateJob(operationId, SleepingImage, activeDeadlineSeconds: 240);

        try
        {
            // 1. Submit the long-running worker and wait until it is actually Running on the cluster.
            var submission = await backend.StartAsync(job);
            submission.Status.Should().BeOneOf(ExecutionJobStatus.Provisioning, ExecutionJobStatus.Queued);

            var observedJob = MergeResolvedParameters(job, submission);
            var running = await PollUntilAsync(
                backend,
                observedJob,
                static status => status == ExecutionJobStatus.Running,
                TimeSpan.FromMinutes(3));
            running.Status.Should().Be(ExecutionJobStatus.Running, "the sleep worker stays active until cancelled");

            // 2. Cancel the running Job: the backend deletes the provider Job and reports Cancelled.
            var cancelObservedJob = observedJob with { ProviderOperationId = running.ProviderOperationId };
            var cancelResult = await backend.CancelAsync(cancelObservedJob);
            cancelResult.Status.Should().Be(
                ExecutionJobStatus.Cancelled,
                "cancelling a still-running Kubernetes Job must surface Cancelled");
        }
        finally
        {
            await TryCancelAsync(backend, job);
        }
    }

    private static async Task<BatchComputeObservation> PollUntilTerminalAsync(
        KubernetesJobBatchComputeBackend backend,
        ExecutionJobRecord job,
        TimeSpan timeout)
        => await PollUntilAsync(
            backend,
            job,
            static status => status is ExecutionJobStatus.Succeeded
                or ExecutionJobStatus.Failed
                or ExecutionJobStatus.Cancelled,
            timeout);

    private static async Task<BatchComputeObservation> PollUntilAsync(
        KubernetesJobBatchComputeBackend backend,
        ExecutionJobRecord job,
        Func<ExecutionJobStatus, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        BatchComputeObservation last = new() { Status = ExecutionJobStatus.Queued };

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await backend.ObserveAsync(job);
            if (predicate(last.Status))
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
            $"Kubernetes Job did not satisfy the expected condition within {timeout}. Last status: {last.Status}.");
    }

    private static async Task TryCancelAsync(KubernetesJobBatchComputeBackend backend, ExecutionJobRecord job)
    {
        try
        {
            await backend.CancelAsync(job);
        }
        catch
        {
            // Cleanup is best-effort; the cluster is ephemeral CI infrastructure and the Job
            // carries an activeDeadlineSeconds/TTL safety net.
        }
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

    private static ExecutionJobRecord CreateJob(string operationId, string image, int? activeDeadlineSeconds = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KubernetesJobParameterKeys.Image] = image,
            // The image is preloaded into the kind node by the workflow, so never attempt a
            // registry pull (which would fail for the local-only honua-ci/* tags).
            [KubernetesJobParameterKeys.ImagePullPolicy] = "Never"
        };
        if (activeDeadlineSeconds is { } deadline)
        {
            parameters[KubernetesJobParameterKeys.ActiveDeadlineSeconds] =
                deadline.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = KubernetesJobBatchComputeBackend.BackendId,
            WorkloadId = "ci-harness-workload",
            WorkloadName = "cloud-integration-gp",
            Artifact = image,
            Parameters = parameters
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
