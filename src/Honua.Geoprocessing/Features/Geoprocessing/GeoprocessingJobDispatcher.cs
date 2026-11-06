// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Security;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

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

internal readonly record struct RemoteCancelResult(
    RemoteCancelOutcome Outcome,
    ExecutionJobStatus? TerminalStatus = null);

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
    private readonly IOperationEnvelopeFactory? _operationEnvelopeFactory;
    private readonly ExecutionAdmissionCoordinator _admissionCoordinator;

    internal IGeoprocessingHostRuntime HostRuntime { get; init; } = ProcessEnvironmentHostRuntime.Instance;

    public GeoprocessingJobDispatcher(
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IUniversalProgressStore progressStore,
        IJobQueue? jobQueue = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null,
        IExecutionAdmissionEvaluator? admissionEvaluator = null,
        IOperationGateway? operationGateway = null,
        IOperationEnvelopeFactory? operationEnvelopeFactory = null,
        ExecutionAdmissionCoordinator? admissionCoordinator = null)
    {
        _logger = logger;
        _executorOptions = executorOptions;
        _progressStore = progressStore;
        _jobQueue = jobQueue;
        _workloadRegistry = workloadRegistry;
        _backends = backends?.ToArray() ?? Array.Empty<IBatchComputeBackend>();
        _admissionEvaluator = admissionEvaluator;
        _operationGateway = operationGateway;
        _operationEnvelopeFactory = operationEnvelopeFactory;
        _admissionCoordinator = admissionCoordinator ?? ExecutionAdmissionCoordinator.ProcessLocal;
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    // Admission reads and record creation hold this lease, and it is released before backend submission so that work is not serialized.
    public Task<ExecutionAdmissionCoordinator.Lease> EnterAdmissionWindowAsync(CancellationToken cancellationToken)
        => _admissionCoordinator.AcquireAsync(cancellationToken);

    public async Task<ExecutionAdmissionDecision?> EnsureAdmittedAsync(
        string? ownerId,
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
            PrincipalId = ExecutionAdmissionIdentity.ResolveRatePrincipal(ownerId, partitionKey),
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

    // Custom-code is not stored as an approval proposal because resume cannot re-mint the scoped callback token without the live principal.
    public async Task CreateApprovalProposalOrThrowAsync(
        string policyRef,
        AnalysisPlan plan,
        string? idempotencyKey,
        string? requestedBy,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool isCustomCode,
        string? approvalGatedProcessId,
        JobSecurityContext? submitterSecurityContext,
        CancellationToken cancellationToken)
    {
        if (_operationGateway == null || _operationEnvelopeFactory == null || isCustomCode)
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
            // Persist the submitter's row/field security identity with the proposal so the
            // resumed job enforces the ORIGINAL submitter's RLS and masking (#3068).
            SubmitterSecurityContext = submitterSecurityContext,
        };

        // The envelope and gateway derive the proposal identity from the idempotency key and
        // tenant alone, so scope the key to the durable submitter before it reaches them: two
        // actors reusing one client key must not resolve to the same proposal. The payload keeps
        // the client key, from which the resumed job derives its owner-scoped job id.
        var proposalKey = JobOwnershipSecurity.CreateOwnerScopedKey(
            payload.IdempotencyKey, requestedBy, submitterSecurityContext?.TenantId);
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.Geoprocess,
            RequestedBy = payload.RequestedBy,
            Reason = approvalGatedProcessId == null
                ? "Destructive geoprocessing plan requires approval."
                : $"Geoprocessing plan step '{approvalGatedProcessId}' requires approval.",
            IdempotencyKey = proposalKey,
            ExecutionPayload = payload.Serialize(),
            Plan = GeoprocessOperationExecutor.BuildPlanSummary(payload, executionPayload: null),
        };

        var envelope = await _operationEnvelopeFactory.CreateAcceptedAsync(
                "control-plane.geoprocess",
                new OperationPolicyContext
                {
                    PrincipalId = requestedBy,
                    AuthorizationOutcome = "approval-gate-authorized",
                    IdempotencyKey = proposalKey,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (envelope.Status == OperationHandleStatus.Failed || string.IsNullOrWhiteSpace(envelope.AuditId))
        {
            throw new GeoprocessingApprovalRequiredException(policyRef);
        }

        request = request with
        {
            OperationInstanceId = envelope.OperationInstanceId,
            CorrelationId = envelope.CorrelationId,
        };

        var result = await _operationGateway
            .CreateApprovalProposalAsync(envelope.OperationInstanceId, request, cancellationToken)
            .ConfigureAwait(false);

        throw new GeoprocessingApprovalRequiredException(policyRef, detail: null, proposalId: result.ProposalId);
    }

    // A managed job is not placed on the in-process queue of a serverless host, because that loop stops when the invocation ends.
    public async Task<ExecutionJobDefinition?> ResolveWorkloadAsync(
        bool isCustomCode,
        string? requiredRuntimeProfile,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? confirmKeyedReplay = null)
    {
        if (_workloadRegistry == null)
        {
            if (RefusesServerlessManagedLoop(isCustomCode, requiredRuntimeProfile, selected: null)
                && !await ConfirmKeyedReplayAsync(confirmKeyedReplay, cancellationToken).ConfigureAwait(false))
            {
                throw GeoprocessingStoreUnavailableException.ForServerlessManagedHost();
            }

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

        // A remote image must explicitly support the required runtime. The local
        // baseline uses the shared queue, whose managed/native consumers apply the
        // runtime claim fence, so an unprofiled local workload can serve either.
        // Managed work stays on that local worker when it exists. Preferring any
        // remote image sent geometry.buffer to a native or placeholder Batch
        // definition that never claims it. Native work prefers the workload that
        // declares the native profile, which is the GDAL worker image.
        var profile = RuntimeProfiles.Normalize(requiredRuntimeProfile);
        var compatible = geoprocessingWorkloads.Where(d =>
            (string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(d.RuntimeProfile))
            || string.Equals(RuntimeProfiles.Normalize(d.RuntimeProfile), profile, StringComparison.Ordinal))
            .ToArray();
        var nativeProfile = string.Equals(profile, RuntimeProfiles.Native, StringComparison.Ordinal);
        var serverlessManaged = !nativeProfile && HostRuntime.IsServerless;
        // On a long-lived host the API process is the managed worker, so local wins.
        // On a serverless host that process is frozen after the response, so local is
        // not a placement: only a remote workload that declares the managed profile
        // qualifies, and anything else is refused below rather than queued.
        ExecutionJobDefinition? selected;
        if (nativeProfile)
        {
            selected = compatible.FirstOrDefault(d =>
                    !string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                    && string.Equals(RuntimeProfiles.Normalize(d.RuntimeProfile), RuntimeProfiles.Native, StringComparison.Ordinal))
                ?? compatible.FirstOrDefault(d =>
                    string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal));
        }
        else if (serverlessManaged)
        {
            selected = compatible.FirstOrDefault(d =>
                !string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                && string.Equals(RuntimeProfiles.Normalize(d.RuntimeProfile), profile, StringComparison.Ordinal));
        }
        else
        {
            selected = compatible.FirstOrDefault(d =>
                    string.Equals(d.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
                ?? compatible.FirstOrDefault();
        }
        if (selected is null && geoprocessingWorkloads.Length > 0)
        {
            // An exact retry of a job accepted under the previous workload set does not
            // need a compatible image selected again (#6069).
            if (await ConfirmKeyedReplayAsync(confirmKeyedReplay, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (serverlessManaged)
            {
                throw GeoprocessingStoreUnavailableException.ForServerlessManagedHost();
            }

            throw new GeoprocessingValidationException(
                $"No geoprocessing workload supports runtime profile '{profile}'. Configure a compatible worker image and runtime profile.");
        }

        if (selected is null
            && RefusesServerlessManagedLoop(isCustomCode, requiredRuntimeProfile, selected)
            && !await ConfirmKeyedReplayAsync(confirmKeyedReplay, cancellationToken).ConfigureAwait(false))
        {
            throw GeoprocessingStoreUnavailableException.ForServerlessManagedHost();
        }

        return selected;
    }

    private bool RefusesServerlessManagedLoop(
        bool isCustomCode,
        string? requiredRuntimeProfile,
        ExecutionJobDefinition? selected)
    {
        if (isCustomCode || !HostRuntime.IsServerless)
        {
            return false;
        }

        var profile = RuntimeProfiles.Normalize(requiredRuntimeProfile);
        if (string.Equals(profile, RuntimeProfiles.Native, StringComparison.Ordinal)
            || string.Equals(profile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal))
        {
            return false;
        }

        return selected is null
            || string.Equals(selected.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal);
    }

    private static Task<bool> ConfirmKeyedReplayAsync(
        Func<CancellationToken, Task<bool>>? confirmKeyedReplay,
        CancellationToken cancellationToken)
        => confirmKeyedReplay == null
            ? Task.FromResult(false)
            : confirmKeyedReplay(cancellationToken);

    public Task<ExecutionJobRecord> MaybeEnqueueLocalAsync(
        ExecutionJobRecord job, IExecutionJobStore jobStore, CancellationToken cancellationToken)
        => RepairLocalDispatchAsync(job, jobStore, cancellationToken, newlyCreated: true);

    public async Task<ExecutionJobRecord> RepairLocalDispatchAsync(
        ExecutionJobRecord job, IExecutionJobStore jobStore, CancellationToken cancellationToken,
        bool admissionWindowHeld = false, bool newlyCreated = false)
    {
        if (_jobQueue == null || !ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(job)
            || job.CurrentPhase == ExecutionJobSubmissionHelper.LocalDispatchAcceptedPhase)
        {
            return job;
        }

        if (!admissionWindowHeld)
        {
            await using var window = await EnterAdmissionWindowAsync(cancellationToken).ConfigureAwait(false);
            await window.EnsureHeldAsync().ConfigureAwait(false);
            return await RepairLocalDispatchAsync(job, jobStore, cancellationToken,
                admissionWindowHeld: true, newlyCreated: newlyCreated).ConfigureAwait(false);
        }

        var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        if (current == null && newlyCreated)
        {
            // The caller has just persisted this record successfully.
            current = job;
        }
        if (current == null || !ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(current)
            || current.CurrentPhase == ExecutionJobSubmissionHelper.LocalDispatchAcceptedPhase)
        {
            return current ?? job;
        }

        await _jobQueue.EnqueueAsync(current.OperationId, current.Priority, cancellationToken).ConfigureAwait(false);

        // The queue accepted the delivery. Complete its durable acknowledgement
        // independently of request cancellation; missing acknowledgements remain
        // recoverable through the idempotent queue and active-job sweep.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var accepted = current with
                {
                    CurrentPhase = ExecutionJobSubmissionHelper.LocalDispatchAcceptedPhase,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                if (await jobStore.TrySetAsync(accepted, cancellationToken: completion.Token).ConfigureAwait(false))
                {
                    return await jobStore.GetAsync(job.OperationId, completion.Token).ConfigureAwait(false) ?? accepted;
                }
                var latest = await jobStore.GetAsync(job.OperationId, completion.Token).ConfigureAwait(false);
                if (latest == null || !ExecutionJobSubmissionHelper.NeedsLocalDispatchRepair(latest)
                    || latest.CurrentPhase == ExecutionJobSubmissionHelper.LocalDispatchAcceptedPhase)
                {
                    return latest ?? current;
                }
                current = latest;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // The delivery is already on the queue, so a slow or stalled store must not turn an
            // accepted submission into a failure: the caller would roll the record back and
            // answer 408 while a worker claims and runs the job. The record is still initial
            // local Queued dispatch intent, which the active-job sweep repairs.
            GeoprocessingServiceLog.LocalDispatchAcknowledgementDeferred(_logger, current.OperationId, ex);
        }
        return current;
    }

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
