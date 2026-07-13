// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing.CustomCode;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Outcome of an attempt to cancel a job on its remote batch compute backend.
/// </summary>
internal enum RemoteCancelOutcome
{
    Delegated,
    TerminalConflict,
    Missing,
    Unconfirmed,
    Unsupported,
    BackendNotFound,
    NotRemote
}

/// <summary>
/// Result of a remote cancel attempt: the <see cref="RemoteCancelOutcome"/> and, for a
/// terminal-conflict outcome, the observed terminal status.
/// </summary>
internal readonly record struct RemoteCancelResult(
    RemoteCancelOutcome Outcome,
    ExecutionJobStatus? TerminalStatus = null);

/// <summary>
/// Compute-placement and execution-dispatch runtime for <see cref="GeoprocessingJobService"/>:
/// execution admission, workload (job-definition) selection, the local in-process queue, and
/// remote batch-compute-backend submission and cancellation. Owns the optional
/// <see cref="IJobQueue"/>, <see cref="IExecutionJobDefinitionRegistry"/>,
/// <see cref="IBatchComputeBackend"/> set, and <see cref="IExecutionAdmissionEvaluator"/>
/// collaborators (each default-off when the supporting infrastructure is absent) plus the
/// shared <see cref="IUniversalProgressStore"/> used to bridge backend progress. The durable
/// <see cref="IExecutionJobStore"/> stays owned by the job service and is threaded in per call
/// so this dispatcher carries no job-CRUD surface. Behavior, ordering, CAS semantics, logging,
/// and the surfaced exceptions are identical to the inline logic the service previously
/// performed.
/// </summary>
internal sealed class GeoprocessingJobDispatcher
{
    // The initial serving↔worker job contract. A job at this version runs on any worker because
    // every backend reports at least this via BatchComputeBackendCapabilities.MaxSupportedContractVersion.
    private const int BaselineContractVersion = 1;

    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IExecutionJobDefinitionRegistry? _workloadRegistry;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly IExecutionAdmissionEvaluator? _admissionEvaluator;

    /// <summary>
    /// Creates the dispatcher over the admission evaluator, workload registry, queue, and
    /// batch compute backends, falling back to null-object/empty semantics when any are absent.
    /// </summary>
    public GeoprocessingJobDispatcher(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IUniversalProgressStore progressStore,
        IJobQueue? jobQueue = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null,
        IExecutionAdmissionEvaluator? admissionEvaluator = null,
        IEnumerable<IProcessExecutor>? processExecutors = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _progressStore = progressStore;
        _jobQueue = jobQueue;
        _workloadRegistry = workloadRegistry;
        _backends = backends?.ToArray() ?? Array.Empty<IBatchComputeBackend>();
        _admissionEvaluator = admissionEvaluator;
        ManagedProcessIds = processExecutors is null
            ? null
            : processExecutors
                .SelectMany(executor => executor.ProcessIds)
                .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Process ids of the registered managed <see cref="IProcessExecutor"/>s — the exact id set
    /// this dispatcher routes at execution time. Owned here (rather than injected separately
    /// into <see cref="GeoprocessingJobService"/>) so plan validation can flag sync-only catalog
    /// processes against the same routing truth without the service taking a duplicate executor
    /// dependency (#2806); <c>null</c> when no executor set was supplied (test construction).
    /// </summary>
    public IReadOnlySet<string>? ManagedProcessIds { get; }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    /// <summary>
    /// Evaluates execution admission for the submission. Returns the admitted decision, or
    /// <c>null</c> when no admission evaluator is configured. Throws
    /// <see cref="GeoprocessingAdmissionException"/> (after logging) when admission denies the request.
    /// </summary>
    public async Task<ExecutionAdmissionDecision?> EnsureAdmittedAsync(
        ClaimsPrincipal principal,
        string? partitionKey,
        double costWeight,
        OperationPriority priority,
        CancellationToken cancellationToken)
    {
        if (_admissionEvaluator == null)
        {
            return null;
        }

        var request = new ExecutionAdmissionRequest
        {
            JobKind = ExecutionJobKind.Geoprocessing,
            PartitionKey = partitionKey,
            PrincipalId = principal.Identity?.Name,
            EstimatedCostWeight = costWeight,
            Priority = priority
        };

        var decision = await _admissionEvaluator
            .EvaluateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (decision.Outcome == ExecutionAdmissionOutcome.Admitted)
        {
            return decision;
        }

        GeoprocessingServiceLog.SubmitRejectedByAdmission(
            _logger,
            decision.Outcome.ToString(),
            decision.DenyingDimension?.ToString() ?? "Unknown",
            decision.PolicyRef ?? "unknown");

        throw new GeoprocessingAdmissionException(
            decision.Outcome,
            decision.DenyingDimension ?? ExecutionAdmissionDimension.Backpressure,
            decision.PolicyRef ?? "unknown",
            decision.Reason ?? "Execution admission rejected the request.",
            decision.RetryAfterSeconds ?? 10);
    }

    /// <summary>
    /// Resolves the execution workload (job definition) a submission should run under, routing
    /// custom-code jobs to the custom-code runtime profile and preferring a configured remote
    /// geoprocessing workload over the always-present local baseline. Returns <c>null</c> when
    /// no workload registry is configured.
    /// </summary>
    public async Task<ExecutionJobDefinition?> ResolveWorkloadAsync(bool isCustomCode, CancellationToken cancellationToken)
    {
        if (_workloadRegistry == null)
        {
            return null;
        }

        var definitions = await _workloadRegistry.ListAsync(cancellationToken).ConfigureAwait(false);

        if (isCustomCode)
        {
            // Route a custom-code job to the workload that declares the custom-code
            // runtime profile (the Batch tier/queue params, NO secretsmanager env
            // refs — those are built into the job-def family by the iac). The runtime
            // selector (customcode.runtime = python|dotnet) flows through the spec
            // parameters and the iac job-def family resolves it to the matching image;
            // the routing fence itself is runtime-agnostic. Falls back to null when not configured
            // so submission fails cleanly rather than landing on the GP workload.
            return definitions.FirstOrDefault(d =>
                d.Kind == ExecutionJobKind.Geoprocessing &&
                string.Equals(d.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal));
        }

        // An ordinary geoprocessing job must NOT pick up the custom-code workload.
        var geoprocessingWorkloads = definitions
            .Where(d => d.Kind == ExecutionJobKind.Geoprocessing &&
                        !string.Equals(d.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal))
            .ToArray();

        // When the operator has supplied a remote (e.g. AWS Batch) GP workload
        // alongside the always-present local/Kubernetes baseline, prefer the remote
        // one so a fully-configured substrate routes GP execution off-box. The
        // registry already drops remote workloads that are missing their required
        // ARNs (see ExecutionWorkloadGate), so a surviving non-local workload is one
        // the operator deliberately activated. Falling back to FirstOrDefault keeps
        // the local-only default behavior when no remote workload is configured.
        return geoprocessingWorkloads.FirstOrDefault(d =>
                   !string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
               ?? geoprocessingWorkloads.FirstOrDefault();
    }

    /// <summary>
    /// Enqueues the job on the local in-process queue when a queue is configured and the job
    /// targets the local backend. No-ops otherwise.
    /// </summary>
    public async Task MaybeEnqueueLocalAsync(string jobId, string backend, CancellationToken cancellationToken)
    {
        if (_jobQueue != null && string.Equals(backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort removal of a job from the local queue, logging and swallowing failures so the
    /// stale-claim reconciler repairs any residue. No-ops when no queue is configured.
    /// </summary>
    public async Task TryRemoveFromQueueAsync(string jobId, CancellationToken cancellationToken)
    {
        if (_jobQueue == null)
        {
            return;
        }

        try
        {
            await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
        }
    }

    /// <summary>
    /// Submits a job to its remote batch compute backend (or returns it unchanged for the local
    /// backend). Throws when the targeted backend is not registered.
    /// </summary>
    public async Task<ExecutionJobRecord> TrySubmitToBackendAsync(
        ExecutionJobRecord job,
        IExecutionJobStore jobStore,
        CancellationToken cancellationToken)
    {
        if (string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            return job;
        }

        var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            throw new InvalidOperationException(
                $"No batch compute backend registered for '{job.Spec.Backend}' ({job.Spec.TargetKind}).");
        }

        // Serving↔worker job-contract gate (ADR-0060 principle #3b): during a rolling version step
        // a vX server must not submit a job an older (vY) worker cannot run. Fail the job cleanly
        // rather than dispatching a payload the worker would reject, so operators see a clear reason.
        // A baseline (v1) job runs on any worker (every backend supports at least the initial
        // contract), so only consult capabilities when the job requires a newer contract.
        if (job.Spec.ContractVersion > BaselineContractVersion)
        {
            var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (job.Spec.ContractVersion > capabilities.MaxSupportedContractVersion)
            {
                GeoprocessingServiceLog.SubmitRejectedByContractVersion(
                    _logger,
                    job.OperationId,
                    job.Spec.Backend,
                    job.Spec.ContractVersion,
                    capabilities.MaxSupportedContractVersion);

                var mismatchMessage =
                    $"Job requires job-contract version {job.Spec.ContractVersion} but backend "
                    + $"'{job.Spec.Backend}' supports at most version {capabilities.MaxSupportedContractVersion}. "
                    + "Complete the worker upgrade before submitting this job.";

                await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                    jobStore,
                    job.OperationId,
                    _progressStore,
                    ProgressRetention,
                    mismatchMessage,
                    cancellationToken).ConfigureAwait(false);

                var failed = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
                return failed ?? job;
            }
        }

        return await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            job, backend, jobStore, _progressStore, ProgressRetention, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to cancel a job on its remote batch compute backend, applying the durable CAS
    /// guards (pre-submission cancel, cancellation-requested stamp, backend observation merge)
    /// and bridging progress. Returns <see cref="RemoteCancelOutcome.NotRemote"/> for local jobs
    /// so the caller falls through to the local cancellation path.
    /// </summary>
    public async Task<RemoteCancelResult> TryCancelViaBackendAsync(
        ExecutionJobRecord job,
        IExecutionJobStore jobStore,
        CancellationToken cancellationToken)
    {
        if (string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            return new(RemoteCancelOutcome.NotRemote);
        }

        if (GeoprocessingJobService.IsTerminal(job.Status))
        {
            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                _progressStore, job, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
            return job.Status == ExecutionJobStatus.Cancelled
                ? new(RemoteCancelOutcome.Delegated)
                : new(RemoteCancelOutcome.TerminalConflict, job.Status);
        }

        var backend = _backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            return new(RemoteCancelOutcome.BackendNotFound);
        }

        var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.SupportsCancellation)
        {
            return new(RemoteCancelOutcome.Unsupported);
        }

        if (job.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.HasSubmittedProviderMarker(job))
        {
            var preResult = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(
                jobStore, job, cancellationToken).ConfigureAwait(false);

            switch (preResult.Outcome)
            {
                case PreSubmissionCancelOutcome.Cancelled:
                    await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                        _progressStore, preResult.Job!, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new(RemoteCancelOutcome.Delegated);
                case PreSubmissionCancelOutcome.TerminalConflict:
                    await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                        _progressStore, preResult.Job!, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new(RemoteCancelOutcome.TerminalConflict, preResult.Job!.Status);
                case PreSubmissionCancelOutcome.Missing:
                    return new(RemoteCancelOutcome.Missing);
                default:
                    return new(RemoteCancelOutcome.Unconfirmed);
            }
        }

        // Stamp CancellationRequestedAt before the remote cancel so a concurrent caller
        // that races ahead and sees the provider job already gone maps NotFound →
        // Cancelled rather than Failed. Without this, two callers that both observed the
        // same pre-cancel snapshot could have the "saw the delete" observation lose the
        // CAS race to the "saw NotFound" observation.
        var stampResult = await ExecutionJobCancellationHelper.TryStampRemoteCancelRequestedAtAsync(
            jobStore, job, cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (stampResult.Outcome)
        {
            case RemoteCancelStampOutcome.Missing:
                return new(RemoteCancelOutcome.Missing);
            case RemoteCancelStampOutcome.TerminalConflict:
                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                    _progressStore, stampResult.Job!, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(RemoteCancelOutcome.TerminalConflict, stampResult.TerminalStatus ?? stampResult.Job!.Status);
            case RemoteCancelStampOutcome.Unconfirmed:
                GeoprocessingServiceLog.RemoteCancelCasRetry(_logger, job.OperationId);
                return new(RemoteCancelOutcome.Unconfirmed);
        }

        var stampedJob = stampResult.Job!;
        var observation = await backend.CancelAsync(stampedJob, cancellationToken).ConfigureAwait(false);
        var applyResult = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
            jobStore, stampedJob, observation, cancellationToken).ConfigureAwait(false);

        switch (applyResult.Outcome)
        {
            case BackendCancelApplyOutcome.Missing:
                return new(RemoteCancelOutcome.Missing);
            case BackendCancelApplyOutcome.TerminalConflict:
                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                    _progressStore, applyResult.Job!, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new(RemoteCancelOutcome.TerminalConflict, applyResult.TerminalStatus ?? applyResult.Job!.Status);
            case BackendCancelApplyOutcome.Unconfirmed:
                GeoprocessingServiceLog.RemoteCancelCasRetry(_logger, job.OperationId);
                return new(RemoteCancelOutcome.Unconfirmed);
        }

        var updated = applyResult.Job!;

        await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
            _progressStore, updated, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);

        return updated.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed
            ? new(RemoteCancelOutcome.TerminalConflict, updated.Status)
            : new(RemoteCancelOutcome.Delegated);
    }
}
