// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
using Honua.Infrastructure;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Shared implementation of geoprocessing job lifecycle operations.
/// Consumed by gRPC (<see cref="HonuaProcessService"/>) and REST
/// (<c>GPServerEndpoints</c>) adapters.
/// </summary>
internal sealed class GeoprocessingJobService : IGeoprocessingJobService
{
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
    private readonly IExecutionAdmissionEvaluator? _admissionEvaluator;
    private readonly IGeoprocessingResultPackageStore? _resultPackageStore;
    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;

    public GeoprocessingJobService(
        IUniversalProgressStore progressStore,
        IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        IProcessCatalog processCatalog,
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null,
        IOptions<LimitsOptions>? limitsOptions = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null,
        IExecutionAdmissionEvaluator? admissionEvaluator = null,
        IGeoprocessingResultPackageStore? resultPackageStore = null)
    {
        _progressStore = progressStore;
        _cancellationNotifiers = cancellationNotifiers.ToArray();
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _processCatalog = processCatalog;
        _analyticsLimits = limitsOptions?.Value.Analytics ?? new AnalyticsLimits();
        _logger = logger;
        _executorOptions = executorOptions;
        _jobStore = jobStore;
        _jobQueue = jobQueue;
        _workloadRegistry = workloadRegistry;
        _backends = backends?.ToArray() ?? Array.Empty<IBatchComputeBackend>();
        _admissionEvaluator = admissionEvaluator;
        _resultPackageStore = resultPackageStore;
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    public Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
    {
        return EnsureAuthorizedAsync(principal, resourceType, operation, cancellationToken);
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

        // Phase 0 auth spine (Deliverable 1): pin the submitter's owner snapshot
        // when the job declares a custom-code resource scope. The declared scope is
        // validated to be ⊆ what the submitter can reach; anything beyond is
        // rejected here, so the durable snapshot can only ever attenuate (never
        // widen) a later scoped-job callback token. Behavior is unchanged for
        // ordinary jobs, which declare no scope and pin no snapshot.
        var ownerScope = CustomCodeOwnerScopeCapture.TryCapture(
            principal,
            protocolMetadata,
            globalDataEditorRoles: null,
            out var scopeRejection);
        if (scopeRejection is not null)
        {
            GeoprocessingServiceLog.DeclaredScopeRejected(_logger, scopeRejection);
            throw new GeoprocessingValidationException(scopeRejection);
        }

        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        var jobId = CreateJobId(resolvedKey);
        var requestFingerprint = CreateRequestFingerprint(plan);

        var specParams = protocolMetadata != null
            ? new Dictionary<string, string>(protocolMetadata)
            : new Dictionary<string, string>();

        var partitionKey = ResolvePartitionKey(specParams);
        var costWeight = (double)Math.Max(plan.Steps.Count, 1);
        var priority = ResolvePriority(specParams);

        var admission = await EnsureAdmittedAsync(
            plan, principal, partitionKey, costWeight, priority, cancellationToken).ConfigureAwait(false);

        if (admission != null)
        {
            specParams[ExecutionAdmissionEvaluator.CostWeightParameterKey] =
                costWeight.ToString("R", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(partitionKey))
            {
                specParams[ExecutionAdmissionEvaluator.PartitionKeyParameterKey] = partitionKey;
            }
        }

        var workload = await ResolveWorkloadAsync(cancellationToken).ConfigureAwait(false);
        var requiredRuntimeProfile = ResolveRequiredRuntimeProfile(plan);
        var spec = BuildSpec(plan, specParams, workload, requiredRuntimeProfile);

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                IdempotencyKey = resolvedKey,
                RequestedBy = principal.Identity?.Name,
                RequestFingerprint = requestFingerprint,
                CustomCodeOwnerScope = ownerScope
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
                EnsureMatchingIdempotentRequest(existing, requestFingerprint, principal);
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                jobStore,
                jobId,
                progressStore: _progressStore,
                progressRetention: ProgressRetention,
                failureMessage: "Submission failed.",
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
        var geoprocessingWorkloads = definitions
            .Where(d => d.Kind == ExecutionJobKind.Geoprocessing)
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
    /// Resolves the runtime profile a job for <paramref name="plan"/> must run
    /// under, read DATA-DRIVEN from the process catalog rather than hard-coded
    /// here. Each <see cref="ProcessDefinition"/> declares its required
    /// <see cref="ProcessDefinition.RuntimeProfile"/> (managed by default; native
    /// for the out-of-process GDAL <c>gdal.*</c> family). The effective job profile
    /// is the first non-managed profile among the plan's processes — a single plan
    /// step that requires the native worker forces the whole job onto the native
    /// profile so the claim fence routes it to the GDAL worker and away from the
    /// lean dispatcher. Returns <c>null</c> (managed/default) when no process
    /// requires a specialized profile, leaving the spec profile-agnostic.
    /// </summary>
    private string? ResolveRequiredRuntimeProfile(AnalysisPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.ProcessId))
            {
                continue;
            }

            var definition = _processCatalog.GetProcess(step.ProcessId);
            if (definition == null)
            {
                continue;
            }

            var profile = RuntimeProfiles.Normalize(definition.RuntimeProfile);
            if (!string.Equals(profile, RuntimeProfiles.Managed, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    private static ExecutionJobSpec BuildSpec(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        ExecutionJobDefinition? workload,
        string? requiredRuntimeProfile)
    {
        specParams[ExecutionJobParameterKeys.GeoprocessingPlanId] = plan.PlanId;
        var processDefinitions = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ProcessId))
            .Select(step => step.ProcessId!)
            .ToArray();
        if (processDefinitions.Length > 0)
        {
            specParams[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = string.Join(
                ExecutionJobParameterKeys.MetadataListSeparator,
                processDefinitions);
        }

        var outputKinds = plan.Outputs.Select(output => output.ToString()).ToArray();
        if (outputKinds.Length > 0)
        {
            specParams[ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = string.Join(
                ExecutionJobParameterKeys.MetadataListSeparator,
                outputKinds);
        }

        // Project plan step inputs onto the durable spec under a stable prefix so
        // worker-side executors can read their parameters without reaching back into
        // the analysis plan. Only `Geoprocess` steps carry semantic inputs in the
        // first-slice catalog; other kinds are ignored here.
        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || step.Inputs.Count == 0)
            {
                continue;
            }

            foreach (var input in step.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Key))
                {
                    continue;
                }

                var key = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}{stepIndex}.{input.Key}";
                specParams[key] = input.Value ?? string.Empty;
            }
        }

        if (workload == null)
        {
            return new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = $"geoprocessing:{plan.PlanId}",
                // Data-driven native-profile stamping: a catalog process that
                // requires a specialized worker (the gdal.* native family) forces
                // this profile so the claim fence routes the job to the GDAL worker
                // and away from the lean dispatcher. Null leaves the job managed/default.
                RuntimeProfile = requiredRuntimeProfile,
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
            // A catalog-required native profile takes precedence over the workload's
            // declared profile so a native gdal.* step still routes to the GDAL worker;
            // otherwise fall back to the workload's own runtime profile.
            RuntimeProfile = requiredRuntimeProfile ?? workload.RuntimeProfile,
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
        await EnsureAuthorizedAsync(
            principal,
            OperatorResourceType.Job,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

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

        EnsureJobOwnership(job, principal);

        GeoprocessingServiceLog.JobRetrieved(_logger, jobId, job.Status.ToString());
        return job;
    }

    public async Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(
            principal,
            OperatorResourceType.Job,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

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

        EnsureJobOwnership(job, principal);

        if (!IsTerminal(job.Status))
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Job '{jobId}' has not reached a terminal state (current: {job.Status}).");
        }

        var expectedResultPackageId = GeoprocessingResultPackageFactory.CreateResultPackageId(job);
        if (_resultPackageStore != null)
        {
            try
            {
                var storedPackage = await _resultPackageStore
                    .GetAsync(jobId, cancellationToken)
                    .ConfigureAwait(false);
                if (storedPackage != null &&
                    string.Equals(
                        storedPackage.ResultPackageId,
                        expectedResultPackageId,
                        StringComparison.Ordinal))
                {
                    GeoprocessingServiceLog.JobResultsRetrieved(_logger, jobId);
                    return storedPackage;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                GeoprocessingServiceLog.JobResultsStoreReadFailed(_logger, jobId, ex);
            }
        }

        var synthesizedPackage = GeoprocessingResultPackageFactory.Create(job, _processCatalog);

        if (_resultPackageStore != null)
        {
            try
            {
                await _resultPackageStore
                    .SetAsync(jobId, synthesizedPackage, ProgressRetention, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                GeoprocessingServiceLog.JobResultsStoreWriteFailed(_logger, jobId, ex);
            }
        }

        GeoprocessingServiceLog.JobResultsRetrieved(_logger, jobId);
        return synthesizedPackage;
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

        await EnsureAuthorizedAsync(
            principal,
            OperatorResourceType.Job,
            OperatorOperation.Execute,
            cancellationToken).ConfigureAwait(false);

        var jobStore = RequireJobStore();
        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            GeoprocessingServiceLog.JobNotFound(_logger, jobId);
            throw new GeoprocessingNotFoundException($"Job '{jobId}' not found.");
        }

        EnsureJobOwnership(job, principal);

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
            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                _progressStore, job, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
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

        if (!IsTerminal(latest.Status))
        {
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

            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                _progressStore, latest, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                    _progressStore, cancelOutcome.Job!, ProgressRetention, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
    {
        var decision = await _authEvaluator.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                Operation = operation
            },
            cancellationToken).ConfigureAwait(false);

        if (decision.IsAllowed)
        {
            return;
        }

        GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());
        throw new GeoprocessingAuthorizationException(decision.RequiresAuthentication);
    }

    /// <summary>
    /// Enforces that job state, results, and cancellation are scoped to the
    /// principal that submitted the job (threat-model residual #1576). A coarse
    /// <c>Job</c>-level grant authorizes the operation class; this check pins the
    /// specific record to its submitter so one authenticated user cannot read or
    /// cancel another user's jobs. Jobs without a recorded submitter (deployments
    /// running with authentication disabled record no identity) keep the previous
    /// behavior, and the conventional <c>admin</c> role retains full visibility
    /// for operations. Denials surface as not-found so cross-principal probing
    /// cannot confirm that a job identifier exists.
    /// </summary>
    private void EnsureJobOwnership(ExecutionJobRecord job, ClaimsPrincipal principal)
    {
        var owner = job.Audit.RequestedBy;
        if (string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        var caller = principal.Identity?.Name;
        if (string.Equals(owner, caller, StringComparison.Ordinal))
        {
            return;
        }

        if (principal.IsInRole("admin"))
        {
            return;
        }

        GeoprocessingServiceLog.JobOwnershipDenied(_logger, job.OperationId);
        throw new GeoprocessingNotFoundException($"Job '{job.OperationId}' not found.");
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

    private async Task<ExecutionAdmissionDecision?> EnsureAdmittedAsync(
        AnalysisPlan plan,
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

    private static string? ResolvePartitionKey(Dictionary<string, string> specParams)
    {
        if (specParams.TryGetValue(ExecutionAdmissionEvaluator.PartitionKeyParameterKey, out var explicitKey)
            && !string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        if (specParams.TryGetValue("workspace.id", out var workspaceId) && !string.IsNullOrWhiteSpace(workspaceId))
        {
            return workspaceId;
        }

        if (specParams.TryGetValue("tenant.id", out var tenantId) && !string.IsNullOrWhiteSpace(tenantId))
        {
            return tenantId;
        }

        return null;
    }

    private static OperationPriority ResolvePriority(Dictionary<string, string> specParams)
    {
        if (specParams.TryGetValue("admission.priority", out var raw)
            && Enum.TryParse<OperationPriority>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return OperationPriority.Normal;
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

        var jobStore = RequireJobStore();

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

    private static void ValidatePlanStructure(AnalysisPlan plan)
        // Structural (dependency-graph) validation is shared with the headless
        // GP Devkit `honua gp plan` dry-run path via AnalysisPlanGraphValidator so
        // both reject the same malformed graphs (dangling/self deps, cycles) with
        // the same message.
        => AnalysisPlanGraphValidator.Validate(plan);

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

    private static void EnsureMatchingIdempotentRequest(
        ExecutionJobRecord existing, string requestFingerprint, ClaimsPrincipal principal)
    {
        // Reject cross-principal replay: a different caller must not silently
        // receive another principal's job via an idempotency-key collision.
        var requestedBy = existing.Audit.RequestedBy;
        var callerName = principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(requestedBy)
            && !string.Equals(requestedBy, callerName, StringComparison.Ordinal))
        {
            throw new GeoprocessingIdempotencyConflictException();
        }

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
