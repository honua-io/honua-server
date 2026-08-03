// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Guardrails.Domain;
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
/// execution admission, workload (job-definition) selection, the local in-process queue,
/// remote batch-compute-backend submission and cancellation, and the approval-lane routing that
/// parks a gated submission as an <see cref="IOperationGateway"/> proposal instead of dispatching
/// it to compute. Owns the optional
/// <see cref="IJobQueue"/>, <see cref="IExecutionJobDefinitionRegistry"/>,
/// <see cref="IBatchComputeBackend"/> set, <see cref="IExecutionAdmissionEvaluator"/>, and
/// <see cref="IOperationGateway"/>
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
    private readonly IOperationGateway? _operationGateway;
    private readonly IRasterExecutionPlanner? _rasterExecutionPlanner;
    private readonly IOptionsMonitor<RasterExecutionPlannerOptions>? _rasterExecutionOptions;
    private readonly IOptionsMonitor<GpWorkloadPlacementOptions>? _workloadPlacementOptions;

    /// <summary>
    /// Creates the dispatcher over the admission evaluator, workload registry, queue,
    /// batch compute backends, and approval-lane operation gateway, falling back to
    /// null-object/empty semantics when any are absent.
    /// </summary>
    public GeoprocessingJobDispatcher(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IUniversalProgressStore progressStore,
        IJobQueue? jobQueue = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null,
        IExecutionAdmissionEvaluator? admissionEvaluator = null,
        IOperationGateway? operationGateway = null,
        IRasterExecutionPlanner? rasterExecutionPlanner = null,
        IOptionsMonitor<RasterExecutionPlannerOptions>? rasterExecutionOptions = null,
        IOptionsMonitor<GpWorkloadPlacementOptions>? workloadPlacementOptions = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _progressStore = progressStore;
        _jobQueue = jobQueue;
        _workloadRegistry = workloadRegistry;
        _backends = backends?.ToArray() ?? Array.Empty<IBatchComputeBackend>();
        _admissionEvaluator = admissionEvaluator;
        _operationGateway = operationGateway;
        _rasterExecutionPlanner = rasterExecutionPlanner;
        _rasterExecutionOptions = rasterExecutionOptions;
        _workloadPlacementOptions = workloadPlacementOptions;
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    /// <summary>
    /// Plans a raster job from metadata, capability, workload availability, health, budgets,
    /// and operator policy. Returns <see langword="null"/> for non-raster plans or when the
    /// optional planner is not composed (legacy/test hosts).
    /// </summary>
    public async Task<RasterExecutionDecision?> PlanRasterExecutionAsync(
        AnalysisPlan plan,
        ProcessDefinition? definition,
        CancellationToken cancellationToken)
    {
        if (definition?.RasterEngineCapabilities is null
            || _rasterExecutionPlanner is null
            || _rasterExecutionOptions is null)
        {
            return null;
        }

        var remoteWorkload = await FindRemoteRasterWorkloadAsync(cancellationToken).ConfigureAwait(false);
        var remoteBackendAvailable = remoteWorkload is not null
            && _backends.Resolve(remoteWorkload.Backend, remoteWorkload.TargetKind) is not null;
        // This backend proves that a remote native lane exists for raster-engine planning. The
        // per-job workload planner later evaluates every compatible remote envelope and the job
        // service finalizes RasterExecutionDecision.Backend with the selected provider.
        var request = RasterExecutionPlanningRequestFactory.Create(
            plan,
            definition.RasterEngineCapabilities,
            _rasterExecutionOptions.CurrentValue,
            remoteBackendAvailable,
            remoteBackendAvailable ? remoteWorkload!.Backend : null);

        try
        {
            return _rasterExecutionPlanner.Plan(request);
        }
        catch (RasterExecutionPlanningException ex)
        {
            throw new GeoprocessingAdmissionException(
                ExecutionAdmissionOutcome.Denied,
                ExecutionAdmissionDimension.Cost,
                $"raster:{ex.ReasonCode}",
                ex.Message,
                retryAfterSeconds: 30);
        }
    }

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
    /// Routes an approval-gated submission onto the approval lane instead of dispatching it to
    /// compute. When a durable proposal surface (<see cref="IOperationGateway"/>) is available and
    /// the submission is not custom code, persists an <c>AwaitingApproval</c> proposal reusing the
    /// shared control-plane proposal/gateway surface so the gated plan is resumable via
    /// <c>honua://proposals/{id}</c> instead of dead-ending (ADR-0064, #2814), then throws
    /// <see cref="GeoprocessingApprovalRequiredException"/> carrying the proposal id. A custom-code
    /// submission is never persisted as a proposal — the resume path cannot re-mint its scoped
    /// callback token without the live principal — so it continues to hard-fail; likewise when the
    /// gateway is unavailable (lightweight hosts / Redis-free configs). This method always throws.
    /// </summary>
    public async Task CreateApprovalProposalOrThrowAsync(
        string policyRef,
        AnalysisPlan plan,
        string? idempotencyKey,
        string? requestedBy,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool isCustomCode,
        string? approvalGatedProcessId,
        CancellationToken cancellationToken)
    {
        if (_operationGateway == null || isCustomCode)
        {
            throw new GeoprocessingApprovalRequiredException(policyRef);
        }

        var payload = new GeoprocessExecutionPayload
        {
            Plan = plan,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            RequestedBy = requestedBy,
            Metadata = protocolMetadata == null
                ? null
                : new Dictionary<string, string>(protocolMetadata, StringComparer.Ordinal),
        };

        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.Geoprocess,
            RequestedBy = payload.RequestedBy,
            Reason = approvalGatedProcessId == null
                ? "Destructive geoprocessing plan requires approval."
                : $"Geoprocessing plan step '{approvalGatedProcessId}' requires approval.",
            IdempotencyKey = payload.IdempotencyKey,
            ExecutionPayload = payload.Serialize(),
            Plan = GeoprocessOperationExecutor.BuildPlanSummary(payload, executionPayload: null),
        };

        var result = await _operationGateway
            .CreateApprovalProposalAsync(request, cancellationToken)
            .ConfigureAwait(false);

        throw new GeoprocessingApprovalRequiredException(policyRef, detail: null, proposalId: result.ProposalId);
    }

    /// <summary>
    /// Resolves the execution workload (job definition) a submission should run under. Custom-code
    /// routing retains its dedicated runtime fence; ordinary jobs are selected per job from
    /// compatible local/remote workloads and receive a durable placement explanation.
    /// </summary>
    public async Task<GpWorkloadPlacementResult> ResolveWorkloadAsync(
        bool isCustomCode,
        string? requiredRuntimeProfile,
        GpResourceProfile resources,
        IReadOnlyDictionary<string, string> requestParameters,
        CancellationToken cancellationToken,
        RasterExecutionDecision? rasterDecision = null)
    {
        var options = _workloadPlacementOptions?.CurrentValue ?? new GpWorkloadPlacementOptions();
        if (_workloadRegistry == null)
        {
            if (isCustomCode)
            {
                return new GpWorkloadPlacementResult(null, null);
            }

            if (rasterDecision?.Placement == RasterExecutionPlacement.RemoteBackend)
            {
                throw RemoteRasterPlacementUnavailable(rasterDecision);
            }

            return GpWorkloadPlacementPlanner.SelectImplicitLocal(
                requiredRuntimeProfile,
                resources,
                requestParameters,
                rasterDecision,
                options);
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
            return new GpWorkloadPlacementResult(
                definitions.FirstOrDefault(d =>
                    d.Kind == ExecutionJobKind.Geoprocessing &&
                    string.Equals(d.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal)),
                null);
        }

        return GpWorkloadPlacementPlanner.Select(
            definitions,
            _backends,
            _jobQueue is not null,
            requiredRuntimeProfile,
            resources,
            requestParameters,
            rasterDecision,
            options);
    }

    private static GeoprocessingAdmissionException RemoteRasterPlacementUnavailable(
        RasterExecutionDecision decision)
        => new(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Backpressure,
            "raster:remote-backend-unavailable",
            $"The pinned raster backend '{decision.Backend}' is no longer available; submit may be retried after backend recovery.",
            retryAfterSeconds: 30);

    private async Task<ExecutionJobDefinition?> FindRemoteRasterWorkloadAsync(
        CancellationToken cancellationToken)
    {
        if (_workloadRegistry == null
            || _workloadPlacementOptions?.CurrentValue.RemoteExecutionEnabled == false)
        {
            return null;
        }

        var definitions = await _workloadRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions
            .Where(IsPotentialRemoteRasterWorkload)
            .OrderBy(definition => definition.Backend, StringComparer.Ordinal)
            .ThenBy(definition => definition.WorkloadId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private bool IsPotentialRemoteRasterWorkload(ExecutionJobDefinition definition)
    {
        if (definition.Kind != ExecutionJobKind.Geoprocessing
            || string.Equals(definition.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal)
            || string.Equals(definition.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            || definition.TargetKind == BatchComputeTargetKind.LocalProcess
            || _backends.Resolve(definition.Backend, definition.TargetKind) is null)
        {
            return false;
        }

        var executionClass = ReadPlacementDeclaration(
            definition.Parameters,
            GpWorkloadPlacementParameterKeys.ExecutionClass);
        if (executionClass is not null and not "remote")
        {
            return false;
        }

        if (definition.Parameters.TryGetValue(GpWorkloadPlacementParameterKeys.Enabled, out var enabledRaw)
            && (!bool.TryParse(enabledRaw, out var enabled) || !enabled))
        {
            return false;
        }

        var capacity = ReadPlacementDeclaration(
            definition.Parameters,
            GpWorkloadPlacementParameterKeys.Capacity);
        if (capacity is not null and not "healthy")
        {
            return false;
        }

        if (definition.Parameters.TryGetValue(
                GpWorkloadPlacementParameterKeys.RuntimeProfiles,
                out var runtimeProfilesRaw)
            && !string.IsNullOrWhiteSpace(runtimeProfilesRaw))
        {
            return runtimeProfilesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(profile => string.Equals(profile, RuntimeProfiles.Native, StringComparison.OrdinalIgnoreCase));
        }

        // Unspecified profiles normalize to managed at the worker claim fence, so they
        // cannot prove that a remote native raster lane exists.
        return string.Equals(
            RuntimeProfiles.Normalize(definition.RuntimeProfile),
            RuntimeProfiles.Native,
            StringComparison.Ordinal);
    }

    private static string? ReadPlacementDeclaration(
        IReadOnlyDictionary<string, string> parameters,
        string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim().ToLowerInvariant()
            : null;

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: the documented best-effort removal (see the XML doc
            // above) — logged so the stale-claim reconciler's later fix-up is diagnosable.
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
                    cancellationToken: cancellationToken).ConfigureAwait(false);

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
