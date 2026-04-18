// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Shared implementation of geoprocessing job lifecycle operations.
/// Consumed by gRPC (<see cref="HonuaProcessService"/>) and REST
/// (<c>GPServerEndpoints</c>) adapters.
/// </summary>
internal sealed class GeoprocessingJobService : IGeoprocessingJobService
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    private readonly IExecutionJobStore? _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IReadOnlyList<IJobCancellationNotifier> _cancellationNotifiers;
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly IProcessCatalog _processCatalog;
    private readonly AnalyticsLimits _analyticsLimits;
    private readonly IExecutionJobDefinitionRegistry? _workloadRegistry;
    private readonly IReadOnlyList<IBatchComputeBackend> _backends;
    private readonly ILogger<GeoprocessingJobService> _logger;

    public GeoprocessingJobService(
        IUniversalProgressStore progressStore,
        IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        IProcessCatalog processCatalog,
        ILogger<GeoprocessingJobService> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null,
        IOptions<LimitsOptions>? limitsOptions = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null)
    {
        _progressStore = progressStore;
        _cancellationNotifiers = cancellationNotifiers.ToArray();
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _processCatalog = processCatalog;
        _analyticsLimits = limitsOptions?.Value.Analytics ?? new AnalyticsLimits();
        _logger = logger;
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _workloadRegistry = workloadRegistry;
        _backends = backends?.ToArray() ?? Array.Empty<IBatchComputeBackend>();
    }

    public void EnsureCallerAuthorized(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        EnsureAuthorized(principal, resourceType, operation);
    }

    public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
    {
        ValidatePlanStructure(plan);

        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_PLAN_ID",
                Message = "Plan identifier is required.",
                FieldPath = "plan_id"
            });
        }

        if (plan.Steps.Count == 0)
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "EMPTY_STEPS",
                Message = "Plan must contain at least one step.",
                FieldPath = "steps"
            });
        }

        var (catalogViolations, catalogWarnings) = ProcessPlanValidator.Validate(plan, _processCatalog, _analyticsLimits);
        violations.AddRange(catalogViolations);
        warnings.AddRange(catalogWarnings);

        foreach (var v in catalogViolations)
        {
            if (v.Code == "UNKNOWN_PROCESS")
            {
                GeoprocessingServiceLog.UnknownProcessReferenced(_logger, v.FieldPath ?? "", v.Message);
            }
        }

        var destructiveProcessId = ProcessDestructiveClassifier.FindFirstDestructiveProcessId(plan);
        if (destructiveProcessId != null)
        {
            GeoprocessingServiceLog.DestructivePlanDetected(_logger, plan.PlanId ?? "", destructiveProcessId);
        }

        var approvalReq = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute,
                IsDestructive = destructiveProcessId != null
            });

        var result = new PlanValidationResult
        {
            IsExecutable = violations.Count == 0,
            RequiresApproval = approvalReq.IsRequired,
            Violations = violations,
            Warnings = warnings
        };

        GeoprocessingServiceLog.PlanValidated(_logger, plan.PlanId ?? "", result.IsExecutable, violations.Count);

        return result;
    }

    public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
    {
        ValidatePlanStructure(plan);
        EnsurePlanCatalogValid(plan);

        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 0,
            EstimatedArtifacts = plan.Outputs,
            SideEffects = []
        };

        GeoprocessingServiceLog.DryRunCompleted(_logger, plan.PlanId, result.EstimatedDurationSeconds);

        return result;
    }

    public async Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePlanStructure(plan);
        EnsurePlanExecutable(plan);
        EnsurePlanCatalogValid(plan);
        EnsureApproved(principal, plan);

        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        var jobId = CreateJobId(resolvedKey);
        var requestFingerprint = CreateRequestFingerprint(plan);

        var specParams = protocolMetadata != null
            ? new Dictionary<string, string>(protocolMetadata)
            : new Dictionary<string, string>();

        var workload = await ResolveWorkloadAsync(cancellationToken).ConfigureAwait(false);
        var spec = BuildSpec(plan, specParams, workload);

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = resolvedKey,
                RequestedBy = principal.Identity?.Name,
                RequestFingerprint = requestFingerprint
            },
            Spec = spec
        };

        var created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!created)
        {
            var existing = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                EnsureSubmissionDidNotRollback(existing);
                GeoprocessingServiceLog.JobSubmittedIdempotent(_logger, jobId);
                return existing;
            }

            throw new InvalidOperationException("Failed to create or locate execution job.");
        }

        try
        {
            var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, plan.PlanId);
            await _progressStore.SetProgressAsync(jobId, progress, ProgressRetention, cancellationToken)
                .ConfigureAwait(false);

            if (_jobQueue != null && string.Equals(jobRecord.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
            {
                await _jobQueue.EnqueueAsync(jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            jobRecord = await TrySubmitToBackendAsync(jobRecord, jobStore, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,
                jobId,
                progressStore: _progressStore,
                progressRetention: ProgressRetention,
                failureMessage: $"Submission failed: {ex.Message}",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        GeoprocessingServiceLog.JobSubmitted(_logger, jobId, plan.PlanId);

        return jobRecord;
    }

    private async Task<ExecutionJobDefinition?> ResolveWorkloadAsync(CancellationToken cancellationToken)
    {
        if (_workloadRegistry == null)
        {
            return null;
        }

        var definitions = await _workloadRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions.FirstOrDefault(d => d.Kind == ExecutionJobKind.Geoprocessing);
    }

    private static ExecutionJobSpec BuildSpec(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        ExecutionJobDefinition? workload)
    {
        specParams[ExecutionJobParameterKeys.GeoprocessingPlanId] = plan.PlanId;

        if (workload == null)
        {
            return new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = $"geoprocessing:{plan.PlanId}",
                Parameters = specParams
            };
        }

        foreach (var kv in workload.Parameters)
        {
            specParams.TryAdd(kv.Key, kv.Value);
        }

        return new ExecutionJobSpec
        {
            Kind = workload.Kind,
            TargetKind = workload.TargetKind,
            Backend = workload.Backend,
            WorkloadId = workload.WorkloadId,
            WorkloadName = workload.WorkloadName,
            Artifact = workload.ArtifactReference,
            RuntimeProfile = workload.RuntimeProfile,
            Parameters = specParams
        };
    }

    private async Task<ExecutionJobRecord> TrySubmitToBackendAsync(
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

        return await ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            job, backend, jobStore, _progressStore, ProgressRetention, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        GeoprocessingServiceLog.JobRetrieved(_logger, jobId, job.Status.ToString());
        return job;
    }

    public async Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Read);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        if (!IsTerminal(job.Status))
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' has not reached a terminal state (current: {job.Status}).");
        }

        GeoprocessingServiceLog.JobResultsUnavailable(_logger, jobId);

        throw new GeoprocessingNotFoundException(
            $"Result package for job '{jobId}' is not yet available. " +
            "Result storage will be implemented with the execution engine.");
    }

    public async Task CancelJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new GeoprocessingValidationException("Job identifier is required.");
        }

        EnsureAuthorized(principal, OperatorResourceType.Job, OperatorOperation.Execute);

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            if (_jobQueue != null)
            {
                try
                {
                    await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
                }
            }

            var staleProgress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
                jobId, cancellationToken).ConfigureAwait(false);
            if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
            {
                var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
                await _progressStore.SetProgressAsync(
                    jobId, reconciledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (IsTerminal(job.Status))
        {
            GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, job.Status.ToString());
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' is in terminal state '{job.Status}' and cannot be cancelled.");
        }

        // Cancelling a running job is a destructive action — require approval.
        // Evaluated after state checks so idempotent and terminal paths remain reachable.
        var approval = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                IsDestructive = true
            });

        if (approval.IsRequired)
        {
            GeoprocessingServiceLog.CancelRejectedApprovalRequired(_logger, approval.PolicyRef ?? "unknown");
            throw new GeoprocessingApprovalRequiredException(
                approval.PolicyRef ?? "unknown",
                "Job cancellation requires approval.");
        }

        var workerOwnsTerminalState = _cancellationNotifiers.CancelAny(jobId);

        if (workerOwnsTerminalState)
        {
            GeoprocessingServiceLog.JobCancellationDelegated(_logger, jobId);
            return;
        }

        var latest = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (latest == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException(
                $"Job '{jobId}' was not found on re-read and could not be cancelled.");
        }

        var backendResult = await TryCancelViaBackendAsync(latest, cancellationToken).ConfigureAwait(false);
        switch (backendResult.Outcome)
        {
            case RemoteCancelOutcome.Delegated:
                GeoprocessingServiceLog.JobCancellationDelegated(_logger, jobId);
                return;
            case RemoteCancelOutcome.TerminalConflict:
                var terminalStatus = backendResult.TerminalStatus ?? latest.Status;
                GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, terminalStatus.ToString());
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' reached terminal state '{terminalStatus}' before cancellation could be applied.");
            case RemoteCancelOutcome.Missing:
                GeoprocessingServiceLog.JobNotFound(_logger, jobId);
                throw new GeoprocessingNotFoundException(
                    $"Job '{jobId}' was deleted during cancellation.");
            case RemoteCancelOutcome.Unconfirmed:
                GeoprocessingServiceLog.RemoteCancelCasExhausted(_logger, jobId);
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' remote cancellation could not be confirmed after retries.");
            case RemoteCancelOutcome.Unsupported:
                GeoprocessingServiceLog.RemoteCancelUnavailable(_logger, jobId, latest.Spec.Backend);
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' runs on backend '{latest.Spec.Backend}' which does not support cancellation.");
            case RemoteCancelOutcome.BackendNotFound:
                GeoprocessingServiceLog.RemoteCancelUnavailable(_logger, jobId, latest.Spec.Backend);
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' runs on backend '{latest.Spec.Backend}' which is not registered.");
            case RemoteCancelOutcome.NotRemote:
                break;
        }

        if (IsTerminal(latest.Status))
        {
            if (latest.Status == ExecutionJobStatus.Cancelled)
            {
                if (_jobQueue != null)
                {
                    try
                    {
                        await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
                    }
                }

                var staleProgress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
                    jobId, cancellationToken).ConfigureAwait(false);
                if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
                {
                    var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled");
                    await _progressStore.SetProgressAsync(
                        jobId, reconciledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, latest.Status.ToString());
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' is in terminal state '{latest.Status}' and cannot be cancelled.");
        }

        var cancelOutcome = await ExecutionJobCancellationHelper.TryApplyAsync(
            jobStore,
            jobId,
            latest,
            "Cancelled",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        switch (cancelOutcome.State)
        {
            case ExecutionJobCancellationState.CancellationRequested:
                GeoprocessingServiceLog.JobCancellationDelegated(_logger, jobId);
                return;
            case ExecutionJobCancellationState.TerminalConflict:
                GeoprocessingServiceLog.CancelRejectedTerminal(_logger, jobId, cancelOutcome.Job!.Status.ToString());
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' reached terminal state '{cancelOutcome.Job.Status}' before cancellation could be applied.");
            case ExecutionJobCancellationState.Missing:
                GeoprocessingServiceLog.JobNotFound(_logger, jobId);
                throw new GeoprocessingNotFoundException(
                    $"Job '{jobId}' was deleted during cancellation.");
            case ExecutionJobCancellationState.Unconfirmed:
                throw new GeoprocessingPreconditionFailedException(
                    $"Job '{jobId}' cancellation could not be confirmed after retries.");
            case ExecutionJobCancellationState.Cancelled:
                break;
            default:
                throw new InvalidOperationException($"Unexpected durable cancellation outcome '{cancelOutcome.State}'.");
        }

        var now = DateTimeOffset.UtcNow;

        if (_jobQueue != null)
        {
            try
            {
                await _jobQueue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GeoprocessingServiceLog.QueueRemovalFailed(_logger, jobId, ex);
            }
        }

        var progress = await _progressStore.GetProgressAsync<GeoprocessingProgress>(
            jobId, cancellationToken).ConfigureAwait(false);
        if (progress != null)
        {
            var cancelledProgress = progress.WithCancellation(now, "Cancelled");
            await _progressStore.SetProgressAsync(
                jobId, cancelledProgress, ProgressRetention, cancellationToken).ConfigureAwait(false);
        }

        GeoprocessingServiceLog.JobCancelled(_logger, jobId);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void EnsureAuthorized(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        var decision = _authEvaluator.Evaluate(principal, new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation
        });

        if (decision.IsAllowed)
        {
            return;
        }

        GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());
        throw new GeoprocessingAuthorizationException(decision.RequiresAuthentication);
    }

    private void EnsureApproved(ClaimsPrincipal principal, AnalysisPlan plan)
    {
        var destructiveProcessId = ProcessDestructiveClassifier.FindFirstDestructiveProcessId(plan);
        if (destructiveProcessId != null)
        {
            GeoprocessingServiceLog.DestructivePlanDetected(_logger, plan.PlanId ?? "", destructiveProcessId);
        }

        var approval = _approvalEvaluator.Evaluate(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute,
                IsDestructive = destructiveProcessId != null
            });

        if (!approval.IsRequired)
        {
            return;
        }

        GeoprocessingServiceLog.SubmitRejectedApprovalRequired(_logger, approval.PolicyRef ?? "unknown");
        throw new GeoprocessingApprovalRequiredException(approval.PolicyRef ?? "unknown");
    }

    private IExecutionJobStore RequireJobStore()
        => _jobStore ?? throw new GeoprocessingStoreUnavailableException();

    private enum RemoteCancelOutcome
    {
        Delegated,
        TerminalConflict,
        Missing,
        Unconfirmed,
        Unsupported,
        BackendNotFound,
        NotRemote
    }

    private readonly record struct RemoteCancelResult(
        RemoteCancelOutcome Outcome,
        ExecutionJobStatus? TerminalStatus = null);

    private async Task<RemoteCancelResult> TryCancelViaBackendAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            return new(RemoteCancelOutcome.NotRemote);
        }

        if (IsTerminal(job.Status))
        {
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

        var observation = await backend.CancelAsync(job, cancellationToken).ConfigureAwait(false);
        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var updated = job with
        {
            Status = observation.Status,
            UpdatedAt = now,
            CompletedAt = IsTerminal(observation.Status) ? now : job.CompletedAt,
            ProviderOperationId = observation.ProviderOperationId ?? job.ProviderOperationId,
            PercentComplete = observation.PercentComplete ?? job.PercentComplete,
            CurrentPhase = observation.Message ?? job.CurrentPhase,
            ErrorMessage = observation.Status == ExecutionJobStatus.Failed
                ? observation.Message ?? job.ErrorMessage
                : job.ErrorMessage,
            CancellationRequestedAt = IsTerminal(observation.Status) ? job.CancellationRequestedAt : (job.CancellationRequestedAt ?? now)
        };
        var persisted = await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!persisted)
        {
            GeoprocessingServiceLog.RemoteCancelCasRetry(_logger, job.OperationId);
            var fresh = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            if (fresh == null)
            {
                return new(RemoteCancelOutcome.Missing);
            }

            if (fresh.Status == ExecutionJobStatus.Cancelled)
            {
                updated = fresh;
            }
            else if (IsTerminal(fresh.Status))
            {
                return new(RemoteCancelOutcome.TerminalConflict, fresh.Status);
            }
            else
            {
                var retryNow = DateTimeOffset.UtcNow;
                var retryUpdate = fresh with
                {
                    Status = observation.Status,
                    UpdatedAt = retryNow,
                    CompletedAt = IsTerminal(observation.Status) ? retryNow : fresh.CompletedAt,
                    ProviderOperationId = observation.ProviderOperationId ?? fresh.ProviderOperationId,
                    PercentComplete = observation.PercentComplete ?? fresh.PercentComplete,
                    CurrentPhase = observation.Message ?? fresh.CurrentPhase,
                    ErrorMessage = observation.Status == ExecutionJobStatus.Failed
                        ? observation.Message ?? fresh.ErrorMessage
                        : fresh.ErrorMessage,
                    CancellationRequestedAt = IsTerminal(observation.Status) ? fresh.CancellationRequestedAt : (fresh.CancellationRequestedAt ?? retryNow)
                };
                if (await jobStore.TrySetAsync(retryUpdate, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    updated = retryUpdate;
                }
                else
                {
                    return new(RemoteCancelOutcome.Unconfirmed);
                }
            }
        }

        await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
            _progressStore, updated, ProgressRetention, cancellationToken).ConfigureAwait(false);

        return updated.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed
            ? new(RemoteCancelOutcome.TerminalConflict, updated.Status)
            : new(RemoteCancelOutcome.Delegated);
    }

    private static void ValidatePlanStructure(AnalysisPlan plan)
    {
        // Proto-level enum validation happens in the conversion layer. The remaining
        // domain-level invariant is that the step dependency graph be acyclic and that
        // every dependency refer to a real step — the executor assumes topological
        // ordering, so a cycle or dangling reference deadlocks the run.
        if (plan.Steps.Count == 0)
        {
            return;
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                throw new GeoprocessingValidationException("Every step requires a non-empty stepId.");
            }

            if (!stepIds.Add(step.StepId))
            {
                throw new GeoprocessingValidationException(
                    $"Duplicate step identifier '{step.StepId}'.");
            }
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!stepIds.Contains(dep))
                {
                    throw new GeoprocessingValidationException(
                        $"Step '{step.StepId}' depends on unknown step '{dep}'.");
                }

                if (string.Equals(dep, step.StepId, StringComparison.Ordinal))
                {
                    throw new GeoprocessingValidationException(
                        $"Step '{step.StepId}' cannot depend on itself.");
                }
            }
        }

        // Kahn's algorithm: iteratively remove nodes with zero in-degree; if any remain,
        // the remaining subgraph contains a cycle.
        var inDegree = plan.Steps.ToDictionary(s => s.StepId, _ => 0, StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            edges[step.StepId] = new List<string>();
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                edges[dep].Add(step.StepId);
                inDegree[step.StepId]++;
            }
        }

        var ready = new Queue<string>(inDegree.Where(p => p.Value == 0).Select(p => p.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visited++;
            foreach (var next in edges[current])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        if (visited != plan.Steps.Count)
        {
            throw new GeoprocessingValidationException(
                "Plan step dependency graph contains a cycle.");
        }
    }

    private static void EnsurePlanExecutable(AnalysisPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new GeoprocessingValidationException(
                "Plan identifier is required for job submission.");
        }

        if (plan.Steps.Count == 0)
        {
            throw new GeoprocessingValidationException(
                "Plan must contain at least one step for job submission.");
        }
    }

    private void EnsurePlanCatalogValid(AnalysisPlan plan)
    {
        var (violations, _) = ProcessPlanValidator.Validate(plan, _processCatalog, _analyticsLimits);
        if (violations.Count == 0)
        {
            return;
        }

        foreach (var v in violations)
        {
            if (v.Code == "UNKNOWN_PROCESS")
            {
                GeoprocessingServiceLog.UnknownProcessReferenced(_logger, v.FieldPath ?? "", v.Message);
            }
        }

        var first = violations[0];
        throw new GeoprocessingValidationException(
            $"Plan failed catalog validation: {first.Code} — {first.Message}");
    }

    internal static string CreateJobId(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return $"gp-{Guid.NewGuid():N}";
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"gp-{Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    internal static string CreateRequestFingerprint(AnalysisPlan plan)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("planId", plan.PlanId);
            writer.WriteString("intentId", plan.IntentId);

            writer.WriteStartArray("steps");
            foreach (var step in plan.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("stepId", step.StepId);
                writer.WriteString("kind", step.Kind.ToString());
                writer.WriteString("processId", step.ProcessId ?? "");

                writer.WriteStartArray("inputs");
                foreach (var kv in step.Inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("Key", kv.Key);
                    writer.WriteString("Value", kv.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("dependsOn");
                foreach (var d in step.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(d);
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outputs");
            foreach (var o in plan.Outputs.Select(o => o.ToString()).OrderBy(o => o, StringComparer.Ordinal))
            {
                writer.WriteStringValue(o);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void EnsureMatchingIdempotentRequest(ExecutionJobRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new GeoprocessingIdempotencyConflictException();
    }

    private static void EnsureSubmissionDidNotRollback(ExecutionJobRecord existing)
    {
        if (ExecutionJobSubmissionHelper.IsSubmissionRollback(existing))
        {
            throw new InvalidOperationException(
                $"Job '{existing.OperationId}' submission previously failed before queueing. Retry with a new idempotency key.");
        }
    }

    internal static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;
}
