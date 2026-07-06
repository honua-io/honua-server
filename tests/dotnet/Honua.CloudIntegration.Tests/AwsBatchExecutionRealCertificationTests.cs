// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — the CORE Batch execution cell. Unlike the seeded
/// register/deregister smoke (<see cref="AwsBatchRealCertificationTests"/>), this drives a REAL
/// end-to-end job lifecycle through the PRODUCTION seam: the real
/// <see cref="AwsSdkBatchJobClient"/> behind the real <see cref="AwsBatchComputeBackend"/>, exactly
/// as production wires them (the backend's unit tests construct it the same way, but with a stub
/// client). It certifies that Honua's canonical batch-compute boundary actually submits, observes
/// to a terminal state, and cancels against LIVE AWS Batch — the seam every GP workload rides.
///
/// It submits against the standing certification stack's SMALLEST-tier job definition + queue
/// (honua-iac <c>examples/aws-cert</c>), so no compute environment or job definition is created
/// here; the Fargate task runs to completion and ages out on its own. Per-job vCPU/memory/timeout
/// overrides are exercised through the spec parameter keys (the per-job on-demand model). Every
/// submission's provider job name is derived from an <c>honua-cert-{runId}</c> operation id so a
/// run is traceable; the AWS Batch SubmitJob path the production seam uses does not attach resource
/// tags (the seam sets no tags), and jobs are ephemeral run-to-completion, so there is nothing for
/// the reaper to sweep here — teardown is a guaranteed cancel of any still-running job in a
/// <c>finally</c> block.
///
/// BUDGET: two tiny jobs on the smallest tier, one polled to SUCCEEDED under a hard &lt;8-minute
/// budget and one cancelled promptly. SAFETY: OFF unless
/// <see cref="RealAwsCertificationFixture.BatchExecutionConfigured"/>; every path
/// <c>[SkippableFact]</c>-skips otherwise.
///
/// LIVE-RUN ASSUMPTION (documented risk, not verifiable without AWS): the cert stack's smallest-tier
/// job definition must resolve to a trivially- and quickly-succeeding container (for example a
/// busybox <c>true</c>/<c>echo</c>). The AWS Batch SubmitJob ContainerOverrides the production seam
/// emits can override environment + resource requirements but NOT the container image or command, so
/// the "tiny echo/sleep" shape is a property of the maintainer-provisioned job definition, not of
/// this test. The polled assertion is Succeeded; a job definition that fails fast would surface as a
/// real certification failure by design.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsBatchExecutionRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    // Hard wall-clock budget for the submit→SUCCEEDED poll. Keeps a stuck/misconfigured job from
    // burning the workflow's 30-minute timeout and keeps live cost bounded.
    private static readonly TimeSpan SucceedBudget = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan CancelBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public AwsBatchExecutionRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task SubmitAndObserve_RunsTinyJobToSucceeded_ThroughProductionBackend()
    {
        Skip.IfNot(
            _cert.BatchExecutionConfigured,
            "Real-AWS Batch execution cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true, "
            + "HONUA_REALAWS_CERT_JOB_QUEUE_ARN, HONUA_REALAWS_CERT_JOBDEF_ARN_S with credentials present).");

        var backend = CreateBackend();
        var job = CreateJob("succeed");

        BatchComputeSubmissionResult submission;
        var providerId = (string?)null;
        try
        {
            submission = await backend.StartAsync(job);
            submission.Status.Should().Be(
                ExecutionJobStatus.Queued,
                "a live AWS Batch submission through the production backend must return Queued");
            submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace(
                "live AWS Batch must return a concrete provider job id");
            providerId = submission.ProviderOperationId;

            var observed = job with { Status = submission.Status, ProviderOperationId = providerId };
            var terminal = await PollToTerminalAsync(backend, observed, SucceedBudget);

            terminal.Status.Should().Be(
                ExecutionJobStatus.Succeeded,
                "the smallest-tier certification job definition must run to SUCCEEDED within the budget");
            providerId = null; // reached a terminal state; nothing to tear down.
        }
        finally
        {
            await BestEffortCancelAsync(backend, job, providerId, _output);
        }
    }

    [SkippableFact]
    public async Task SubmitThenCancel_ReachesTerminalCancelled_ThroughProductionBackend()
    {
        Skip.IfNot(
            _cert.BatchExecutionConfigured,
            "Real-AWS Batch execution cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true, "
            + "HONUA_REALAWS_CERT_JOB_QUEUE_ARN, HONUA_REALAWS_CERT_JOBDEF_ARN_S with credentials present).");

        var backend = CreateBackend();
        var job = CreateJob("cancel");

        var providerId = (string?)null;
        try
        {
            var submission = await backend.StartAsync(job);
            submission.ProviderOperationId.Should().NotBeNullOrWhiteSpace();
            providerId = submission.ProviderOperationId;

            var running = job with { Status = submission.Status, ProviderOperationId = providerId };

            // Drive the production cancel path (CancelJob while SUBMITTED/RUNNABLE, TerminateJob
            // otherwise) and then poll until AWS reaches a terminal state.
            var cancelObservation = await backend.CancelAsync(running);
            var terminal = IsTerminal(cancelObservation.Status)
                ? cancelObservation
                : await PollCancelToTerminalAsync(backend, running, CancelBudget);

            IsTerminal(terminal.Status).Should().BeTrue(
                "cancellation must drive the job to a terminal state");
            terminal.Status.Should().BeOneOf(
                new[] { ExecutionJobStatus.Cancelled, ExecutionJobStatus.Succeeded, ExecutionJobStatus.Failed },
                "a promptly-cancelled job resolves to Cancelled, or to Succeeded/Failed if it finished first");
            providerId = null;
        }
        finally
        {
            await BestEffortCancelAsync(backend, job, providerId, _output);
        }
    }

    private static async Task<BatchComputeObservation> PollToTerminalAsync(
        AwsBatchComputeBackend backend,
        ExecutionJobRecord job,
        TimeSpan budget)
    {
        var stopwatch = Stopwatch.StartNew();
        var latest = new BatchComputeObservation { Status = job.Status, ProviderOperationId = job.ProviderOperationId };
        while (stopwatch.Elapsed < budget)
        {
            latest = await backend.ObserveAsync(job);
            if (IsTerminal(latest.Status))
            {
                return latest;
            }

            // Track the provider id in case the backend rebinds it (pending-discovery path).
            job = job with { Status = latest.Status, ProviderOperationId = latest.ProviderOperationId ?? job.ProviderOperationId };
            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(
            $"AWS Batch job '{job.ProviderOperationId}' did not reach a terminal state within {budget}. "
            + $"Last observed status: {latest.Status}.");
    }

    private static async Task<BatchComputeObservation> PollCancelToTerminalAsync(
        AwsBatchComputeBackend backend,
        ExecutionJobRecord job,
        TimeSpan budget)
    {
        var stopwatch = Stopwatch.StartNew();
        // Carry a durable cancellation signal so the backend's discovery paths terminalize correctly.
        var cancelling = job with { CancellationRequestedAt = DateTimeOffset.UtcNow };
        var latest = new BatchComputeObservation { Status = job.Status, ProviderOperationId = job.ProviderOperationId };
        while (stopwatch.Elapsed < budget)
        {
            latest = await backend.ObserveAsync(cancelling);
            if (IsTerminal(latest.Status))
            {
                return latest;
            }

            cancelling = cancelling with { Status = latest.Status, ProviderOperationId = latest.ProviderOperationId ?? cancelling.ProviderOperationId };
            await Task.Delay(PollInterval);
        }

        throw new TimeoutException(
            $"Cancelled AWS Batch job '{job.ProviderOperationId}' did not reach a terminal state within {budget}. "
            + $"Last observed status: {latest.Status}.");
    }

    private static async Task BestEffortCancelAsync(
        AwsBatchComputeBackend backend,
        ExecutionJobRecord job,
        string? providerId,
        ITestOutputHelper output)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        try
        {
            // Guaranteed teardown: cancel/terminate any job left non-terminal by an assertion
            // failure above so the lane never leaves a live Fargate task running.
            await backend.CancelAsync(job with
            {
                ProviderOperationId = providerId,
                CancellationRequestedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            // Best-effort: the job is ephemeral run-to-completion and ages out on its own; a
            // transient cancel blip must not mask the primary assertion outcome. Log it so a failed
            // teardown is visible in CI rather than silently swallowed.
            output.WriteLine(
                $"[cert] best-effort cancel of Batch job '{providerId}' failed: {ex.Message}");
        }
    }

    private AwsBatchComputeBackend CreateBackend()
        => new(new AwsSdkBatchJobClient(), NullLogger<AwsBatchComputeBackend>.Instance);

    private ExecutionJobRecord CreateJob(string purpose)
    {
        var now = DateTimeOffset.UtcNow;

        // The operation id seeds the deterministic provider job name (honua-cert-{runId}-...), so a
        // submitted job is traceable back to this run even though the SubmitJob seam sets no tags.
        var operationId = $"{_cert.ResourcePrefix}-{purpose}";

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Non-tiered path: submit against the smallest-tier job definition directly.
            [AwsBatchParameterKeys.JobDefinitionArn] = _cert.JobDefinitionArnSmall!,
            [AwsBatchParameterKeys.JobQueueArn] = _cert.JobQueueArn!,
            [AwsBatchParameterKeys.Region] = _cert.Region,
            // Per-job on-demand overrides (a valid Fargate vCPU/memory combo) + a short attempt cap
            // so a wedged job cannot outlive the budget.
            [AwsBatchParameterKeys.Vcpus] = "1",
            [AwsBatchParameterKeys.MemoryMib] = "2048",
            [AwsBatchParameterKeys.TimeoutSeconds] = "300",
        };

        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = AwsBatchComputeBackend.AdapterBackendName,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "honua-cert-smoke",
                WorkloadId = "honua-cert-smoke",
                Parameters = parameters,
            },
        };
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;
}
