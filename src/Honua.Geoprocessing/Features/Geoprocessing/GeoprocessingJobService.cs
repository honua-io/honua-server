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
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Shared implementation of geoprocessing job lifecycle operations.
/// Consumed by gRPC (<see cref="HonuaProcessService"/>) and REST
/// (<c>GPServerEndpoints</c>) adapters.
/// </summary>
/// <remarks>
/// The service owns the durable job state (<see cref="IExecutionJobStore"/>,
/// <see cref="IUniversalProgressStore"/>), the worker cancellation notifiers, and the
/// process catalog, and orchestrates submit/cancel/read lifecycles. The remaining
/// cross-cutting concerns are delegated to four cohesive collaborators —
/// <see cref="GeoprocessingJobAuthorizer"/> (authorization/approval),
/// <see cref="GeoprocessingJobDispatcher"/> (admission/queue/workload/backend),
/// <see cref="CustomCodeJobSubmissionGate"/> (custom-code token gate), and
/// <see cref="GeoprocessingJobArtifactService"/> (raster input + result-package artifacts) —
/// so the orchestration here stays focused while behavior is preserved exactly.
/// </remarks>
internal sealed class GeoprocessingJobService : IGeoprocessingJobService
{
    private readonly IExecutionJobStore? _jobStore;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IReadOnlyList<IJobCancellationNotifier> _cancellationNotifiers;
    private readonly IProcessCatalog _processCatalog;
    private readonly AnalyticsLimits _analyticsLimits;
    private readonly GeoprocessingJobAuthorizer _authorizer;
    private readonly GeoprocessingJobDispatcher _dispatcher;
    private readonly CustomCodeJobSubmissionGate _customCodeGate;
    private readonly GeoprocessingJobArtifactService _artifacts;
    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _executorOptions;

    /// <summary>
    /// Production constructor. Composes the durable stores and process catalog with the four
    /// delegating sub-services. The sub-services are registered in DI alongside this type.
    /// </summary>
    public GeoprocessingJobService(
        IUniversalProgressStore progressStore,
        IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        IProcessCatalog processCatalog,
        GeoprocessingJobAuthorizer authorizer,
        GeoprocessingJobDispatcher dispatcher,
        CustomCodeJobSubmissionGate customCodeGate,
        GeoprocessingJobArtifactService artifacts,
        ILogger<GeoprocessingJobService> logger,
        IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
        IExecutionJobStore? jobStore = null,
        IOptions<LimitsOptions>? limitsOptions = null)
    {
        _progressStore = progressStore;
        _cancellationNotifiers = cancellationNotifiers.ToArray();
        _processCatalog = processCatalog;
        _authorizer = authorizer;
        _dispatcher = dispatcher;
        _customCodeGate = customCodeGate;
        _artifacts = artifacts;
        _logger = logger;
        _executorOptions = executorOptions;
        _analyticsLimits = limitsOptions?.Value.Analytics ?? new AnalyticsLimits();
        _jobStore = jobStore;
    }

    /// <summary>
    /// Collaborator-level constructor that composes the delegating sub-services from their raw
    /// dependencies and forwards to the production constructor. Retained so existing call sites
    /// (and direct unit-test construction) can wire the individual collaborators without
    /// building the sub-services by hand. Not used by DI, which binds the production constructor.
    /// </summary>
    internal GeoprocessingJobService(
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
        IGeoprocessingResultPackageStore? resultPackageStore = null,
        IScopedJobTokenIssuer? scopedJobTokenIssuer = null,
        IOptionsMonitor<CustomCodeOptions>? customCodeOptions = null,
        ICustomCodeCommitSignatureVerifier? customCodeSignatureVerifier = null,
        IGeoprocessingRasterSourceResolver? rasterSourceResolver = null)
        : this(
            progressStore,
            cancellationNotifiers,
            processCatalog,
            new GeoprocessingJobAuthorizer(authEvaluator, approvalEvaluator, logger),
            new GeoprocessingJobDispatcher(
                logger, executorOptions, progressStore, jobQueue, workloadRegistry, backends, admissionEvaluator),
            new CustomCodeJobSubmissionGate(
                logger, scopedJobTokenIssuer, customCodeOptions, customCodeSignatureVerifier),
            new GeoprocessingJobArtifactService(
                logger, executorOptions, processCatalog, resultPackageStore, rasterSourceResolver),
            logger,
            executorOptions,
            jobStore,
            limitsOptions)
    {
    }

    private TimeSpan ProgressRetention => _executorOptions.CurrentValue.ResultRetention;

    public Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
    {
        return _authorizer.EnsureAuthorizedAsync(principal, resourceType, operation, cancellationToken);
    }

    public async Task EnsurePlanExecutionTierAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (ContainsMutatingProcess(plan))
        {
            await _authorizer.EnsureAuthorizedAsync(
                principal,
                OperatorResourceType.Process,
                OperatorOperation.ExecuteMutatingProcess,
                cancellationToken).ConfigureAwait(false);
        }
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

        // Surface the direct-submit execution reality (multi-step, sync-only/non-dispatchable
        // process ids, ignored non-Geoprocess step kinds) so the read-only pre-flight reports
        // the same limitations the submit path enforces rather than optimistically returning
        // isExecutable=true for a plan that would be rejected or silently under-execute (#2806).
        var (submitViolations, submitWarnings) = DirectSubmitPlanValidator.Evaluate(plan);
        violations.AddRange(submitViolations);
        warnings.AddRange(submitWarnings);

        foreach (var v in catalogViolations.Where(v => v.Code == "UNKNOWN_PROCESS"))
        {
            GeoprocessingServiceLog.UnknownProcessReferenced(_logger, v.FieldPath ?? "", v.Message);
        }

        var approvalGatedProcessId = ProcessDestructiveClassifier.FindFirstApprovalGatedProcessId(plan, _processCatalog);
        if (approvalGatedProcessId != null)
        {
            GeoprocessingServiceLog.DestructivePlanDetected(_logger, plan.PlanId ?? "", approvalGatedProcessId);
        }

        var approvalReq = _authorizer.EvaluateApproval(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute,
                IsDestructive = approvalGatedProcessId != null
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

        // Prefer the plan's declared outputs; when absent, derive the artifact kinds from
        // the catalog definitions of the plan's Geoprocess steps so the estimate reflects
        // what the processes actually produce instead of an empty list.
        var estimatedArtifacts = plan.Outputs.Count > 0
            ? plan.Outputs
            : DeriveArtifactKinds(plan);

        var sideEffects = DeriveSideEffects(plan);

        // No duration model exists yet, so do NOT fabricate estimatedDurationSeconds=0 as a
        // fact (#2806). Flag the estimate as unavailable and disclose it in plain language via
        // sideEffects so a caller does not read the placeholder 0 as an "instant" prediction.
        sideEffects.Add(
            "No duration estimate is available: the dry-run path does not model runtime cost, "
            + "so estimatedDurationSeconds is a placeholder (0), not a prediction (durationEstimateAvailable=false).");

        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 0,
            DurationEstimateAvailable = false,
            EstimatedArtifacts = estimatedArtifacts,
            SideEffects = sideEffects
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
        // Centralize submit-path authorization here so every adapter (GPServer,
        // OGC Processes, MCP, and the AnalysisContent run/rerun paths) is gated
        // through the shared pipeline rather than relying on caller discipline
        // (#2263). Adapters that already call EnsureCallerAuthorizedAsync before
        // submit stay correct — this evaluation is idempotent and never
        // double-fails an authorized caller.
        await _authorizer.EnsureAuthorizedAsync(
            principal,
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            cancellationToken).ConfigureAwait(false);

        ValidatePlanStructure(plan);
        EnsurePlanExecutable(plan);

        // A custom-code job is param-driven (the user code runs in the Batch
        // container, not against the built-in process catalog), so it carries no
        // catalog process to validate; the customcode.* parameters are validated by
        // the custom-code submit gate below instead. Ordinary jobs still go through
        // the catalog validator.
        var isCustomCode = CustomCodeSubmitValidator.IsCustomCodeSubmission(protocolMetadata);
        if (isCustomCode)
        {
            // #2752: a custom-code submission runs operator-supplied code and therefore
            // requires a dedicated, higher-privilege authorization ON TOP OF the baseline
            // Process.Execute gate above. Without this, any Process.Execute caller could
            // submit allowlisted custom code under the default OrgAllowlist/SignedOnly
            // policies (the prior custom-code-specific who-gate fired only under
            // RepoPolicy.Open). This runs at submission time, before the job is accepted,
            // and is ADDITIVE to the repo-allowlist/signing gates the submit gate enforces.
            await _authorizer.EnsureAuthorizedAsync(
                principal,
                OperatorResourceType.Process,
                OperatorOperation.ExecuteCustomCode,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            EnsurePlanCatalogValid(plan);
        }

        // Evaluate the mutating-process tier unconditionally — including for custom-code
        // submissions, which skip catalog validation. Nothing else asserts that a custom-code
        // plan carries no executable mutating Geoprocess step, so running this for both
        // branches closes the gap where a mutating catalog step smuggled onto a custom-code
        // submission would never face the ExecuteMutatingProcess gate (#2798).
        if (ContainsMutatingProcess(plan))
        {
            await _authorizer.EnsureAuthorizedAsync(
                principal,
                OperatorResourceType.Process,
                OperatorOperation.ExecuteMutatingProcess,
                cancellationToken).ConfigureAwait(false);
        }

        EnsureApproved(principal, plan);

        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        var jobId = CreateJobId(resolvedKey);
        var requestFingerprint = CreateRequestFingerprint(plan);

        // Resolve any native raster/surface step that references a registered catalog
        // raster by layerId/rasterId, materializing the bytes onto the canonical base64
        // 'source' input the worker reads (#2264). The fingerprint above is computed on
        // the caller's original (reference-carrying) plan so idempotency keys map to the
        // request, not the resolved payload; the spec below carries the resolved bytes.
        plan = await _artifacts.ResolveRasterSourcesAsync(plan, cancellationToken).ConfigureAwait(false);

        var specParams = protocolMetadata != null
            ? new Dictionary<string, string>(protocolMetadata)
            : new Dictionary<string, string>();

        // Phase 0/1 auth spine: pin the submitter's owner snapshot when the job
        // declares a custom-code resource scope. The declared scope is validated to
        // be ⊆ what the submitter can reach; anything beyond is rejected, so the
        // durable snapshot can only ever attenuate (never widen) the scoped-job
        // callback token. Behavior is unchanged for ordinary jobs.
        CustomCodeOwnerScope? ownerScope;
        string? mintedCustomCodeToken = null;
        if (isCustomCode)
        {
            (ownerScope, mintedCustomCodeToken) = await _customCodeGate.ValidateMintAndInjectAsync(
                jobId, principal, specParams, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Legacy string-format declared-scope capture (no custom-code runtime
            // marker) — preserved for the Phase-0 metadata path.
            ownerScope = CustomCodeOwnerScopeCapture.TryCapture(
                principal,
                protocolMetadata,
                globalDataEditorRoles: null,
                out var scopeRejection);
            if (scopeRejection is not null)
            {
                GeoprocessingServiceLog.DeclaredScopeRejected(_logger, scopeRejection);
                throw new GeoprocessingValidationException(scopeRejection);
            }
        }

        var partitionKey = ResolvePartitionKey(specParams);
        var costWeight = (double)Math.Max(plan.Steps.Count, 1);
        var priority = ResolvePriority(specParams);

        var admission = await _dispatcher.EnsureAdmittedAsync(
            principal, partitionKey, costWeight, priority, cancellationToken).ConfigureAwait(false);

        if (admission != null)
        {
            specParams[ExecutionAdmissionEvaluator.CostWeightParameterKey] =
                costWeight.ToString("R", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(partitionKey))
            {
                specParams[ExecutionAdmissionEvaluator.PartitionKeyParameterKey] = partitionKey;
            }
        }

        var workload = await _dispatcher.ResolveWorkloadAsync(isCustomCode, cancellationToken).ConfigureAwait(false);
        // A custom-code job forces the custom-code runtime profile so the claim
        // fence routes it to the custom-code Batch workload (and away from the lean
        // dispatcher and the GDAL worker); otherwise stamp the catalog-required profile.
        var requiredRuntimeProfile = isCustomCode
            ? CustomCodeJobContract.RuntimeProfile
            : ResolveRequiredRuntimeProfile(plan);
        // Per-job serverless sizing (#2165): the heaviest catalog-derived resource profile across
        // the plan's steps, overridden by any explicit gp.resource.* request values. Projected onto
        // the spec's batch.* params so AwsBatchComputeBackend.SubmitJob sizes vCPU/memory/timeout/
        // retry/GPU and selects the ephemeral job-def tier per job. Instant and terraform-free.
        var resourceProfile = ResolveResourceProfile(plan, specParams, isCustomCode);
        var spec = BuildSpec(plan, specParams, workload, requiredRuntimeProfile, resourceProfile);

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
                RequestedBy = ResolvePrincipalId(principal),
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

            await _dispatcher.MaybeEnqueueLocalAsync(jobId, jobRecord.Spec.Backend, cancellationToken)
                .ConfigureAwait(false);

            jobRecord = await _dispatcher.TrySubmitToBackendAsync(jobRecord, jobStore, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Revoke the scoped callback token so a credential is never left valid
            // for a job whose submission rolled back (the token must not outlive the
            // job — Phase-0 invariant #5).
            await _customCodeGate.TryRevokeTokenAsync(mintedCustomCodeToken).ConfigureAwait(false);

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

    public async Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        await _authorizer.EnsureAuthorizedAsync(
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

    public async Task<GeoprocessingJobListPage> ListJobsAsync(
        GeoprocessingJobListFilter filter,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(principal);

        await _authorizer.EnsureAuthorizedAsync(
            principal,
            OperatorResourceType.Job,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

        var jobStore = RequireJobStore();
        var limit = Math.Clamp(filter.Limit, 1, MaxJobListPageSize);

        // #2753: scope the store query to the caller for a non-admin so paging
        // is not under-filled. Without this the store returns a page of up-to-`limit` jobs
        // across ALL owners and the ownership post-filter below drops the ones the caller
        // cannot read — so a caller whose jobs are outnumbered by others' could receive a
        // near-empty page even though they own many jobs. Admins are not scoped (they see
        // all). The per-job ownership post-filter is retained as defense in depth.
        var ownerScope = principal.IsInRole("admin") ? null : ResolvePrincipalId(principal);

        // Page the canonical store (newest first, status-filtered there), then apply
        // the adapter binding constraint and per-job ownership in the shared service so
        // no protocol surface can list jobs the caller cannot read. The store cursor is
        // returned verbatim so the client walks the full history without dupes; a page
        // may carry fewer than `limit` items after post-filtering.
        var page = await jobStore.QueryAsync(
            new ExecutionJobQuery
            {
                Kind = ExecutionJobKind.Geoprocessing,
                Statuses = filter.Statuses,
                RequestedBy = ownerScope,
                Cursor = filter.Cursor,
                Limit = limit
            },
            cancellationToken).ConfigureAwait(false);

        var items = page.Items
            .Where(job => MatchesRequiredParameters(job, filter.RequiredParameters) && IsJobReadable(job, principal))
            .ToList();

        GeoprocessingServiceLog.JobsListed(_logger, items.Count);
        return new GeoprocessingJobListPage
        {
            Items = items,
            NextCursor = page.NextCursor
        };
    }

    private const int MaxJobListPageSize = 200;

    private static bool MatchesRequiredParameters(
        ExecutionJobRecord job,
        IReadOnlyDictionary<string, string> required)
    {
        foreach (var (key, value) in required)
        {
            if (!job.Spec.Parameters.TryGetValue(key, out var actual) ||
                !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJobReadable(ExecutionJobRecord job, ClaimsPrincipal principal)
    {
        if (principal.IsInRole("admin"))
        {
            return true;
        }

        var owner = job.Audit.RequestedBy;
        if (string.IsNullOrWhiteSpace(owner))
        {
            // #2753: an ownerless job (empty/null RequestedBy) is readable ONLY by admin.
            // Previously it was readable by anyone, so a coarse Job.Read holder (commonly
            // granted "*") could enumerate jobs whose submitter was never recorded.
            return false;
        }

        return string.Equals(owner, ResolvePrincipalId(principal), StringComparison.Ordinal);
    }

    public async Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        await _authorizer.EnsureAuthorizedAsync(
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

        return await _artifacts.GetOrSynthesizeResultPackageAsync(job, cancellationToken).ConfigureAwait(false);
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

        await _authorizer.EnsureAuthorizedAsync(
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
            await _dispatcher.TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);

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
        var approval = _authorizer.EvaluateApproval(
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
            var backendResult = await _dispatcher.TryCancelViaBackendAsync(latest, jobStore, cancellationToken).ConfigureAwait(false);
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
                await _dispatcher.TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);

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

        await _dispatcher.TryRemoveFromQueueAsync(jobId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Enforces that job state, results, and cancellation are scoped to the
    /// principal that submitted the job (threat-model residual #1576). A coarse
    /// <c>Job</c>-level grant authorizes the operation class; this check pins the
    /// specific record to its submitter so one authenticated user cannot read or
    /// cancel another user's jobs. Jobs without a recorded submitter are readable
    /// only by <c>admin</c> (#2753 closed the prior any-caller read of ownerless
    /// jobs), and the conventional <c>admin</c> role retains full visibility for
    /// operations. Denials surface as not-found so cross-principal probing cannot
    /// confirm that a job identifier exists.
    /// </summary>
    private void EnsureJobOwnership(ExecutionJobRecord job, ClaimsPrincipal principal)
    {
        if (principal.IsInRole("admin"))
        {
            return;
        }

        var owner = job.Audit.RequestedBy;
        var caller = ResolvePrincipalId(principal);
        if (!string.IsNullOrWhiteSpace(owner) &&
            string.Equals(owner, caller, StringComparison.Ordinal))
        {
            return;
        }

        GeoprocessingServiceLog.JobOwnershipDenied(_logger, job.OperationId);
        throw new GeoprocessingNotFoundException($"Job '{job.OperationId}' not found.");
    }

    private static string? ResolvePrincipalId(ClaimsPrincipal principal)
        => principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity?.Name;

    private void EnsureApproved(ClaimsPrincipal principal, AnalysisPlan plan)
    {
        var approvalGatedProcessId = ProcessDestructiveClassifier.FindFirstApprovalGatedProcessId(plan, _processCatalog);
        if (approvalGatedProcessId != null)
        {
            GeoprocessingServiceLog.DestructivePlanDetected(_logger, plan.PlanId ?? "", approvalGatedProcessId);
        }

        var approval = _authorizer.EvaluateApproval(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = OperatorOperation.Execute,
                IsDestructive = approvalGatedProcessId != null
            });

        if (!approval.IsRequired)
        {
            return;
        }

        GeoprocessingServiceLog.SubmitRejectedApprovalRequired(_logger, approval.PolicyRef ?? "unknown");
        throw new GeoprocessingApprovalRequiredException(approval.PolicyRef ?? "unknown");
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

            // Dynamic escalation: a few catalog processes are managed for their
            // in-memory fast paths but require the native PROJ-backed worker for
            // datum-shift inputs. The catalog RuntimeProfile is a STATIC per-process
            // declaration that cannot express "managed for some inputs, native for
            // others", so inspect the step inputs here. transform.reproject escalates
            // to native when the from/to SRID pair is not a managed fast path (i.e. a
            // datum/grid shift): the GDAL worker's GdalVectorReprojectJobExecutor
            // handles the SAME transform.reproject process id under the native profile.
            if (RequiresNativeRuntimeEscalation(step))
            {
                return RuntimeProfiles.Native;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when an otherwise-managed <paramref name="step"/> carries
    /// inputs that force it onto the native worker profile. Currently this covers
    /// <c>transform.reproject</c> jobs whose <c>fromSrid</c>/<c>toSrid</c> pair is a
    /// datum/grid shift the lean executor cannot serve (see
    /// <see cref="ManagedReprojectFastPath"/>). When the SRID inputs are missing or
    /// unparseable, escalation is declined so the managed executor produces the
    /// canonical input-validation error rather than silently routing native.
    /// </summary>
    private static bool RequiresNativeRuntimeEscalation(AnalysisPlanStep step)
    {
        if (!string.Equals(step.ProcessId, ReprojectTransformExecutor.HandledProcessId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!step.Inputs.TryGetValue("fromSrid", out var fromRaw)
            || !step.Inputs.TryGetValue("toSrid", out var toRaw))
        {
            return false;
        }

        if (!ManagedReprojectFastPath.TryParseSrid(fromRaw, out var fromSrid)
            || !ManagedReprojectFastPath.TryParseSrid(toRaw, out var toSrid))
        {
            return false;
        }

        return ManagedReprojectFastPath.RequiresNativeWorker(fromSrid, toSrid);
    }

    /// <summary>
    /// Resolves the effective per-job <see cref="GpResourceProfile"/>: the heaviest catalog-derived
    /// default across the plan's steps, overridden field-by-field by any explicit
    /// <c>gp.resource.*</c> request values. Custom-code jobs are param-driven (no catalog process),
    /// so they take only the explicit request values.
    /// </summary>
    private GpResourceProfile ResolveResourceProfile(
        AnalysisPlan plan,
        IReadOnlyDictionary<string, string> specParams,
        bool isCustomCode)
    {
        var derived = GpResourceProfile.Empty;
        if (!isCustomCode)
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

                derived = derived.MergeMax(GpResourceProfile.ForProcess(definition));
            }
        }

        return derived.OverrideWith(GpResourceProfile.FromRequestParameters(specParams));
    }

    private static ExecutionJobSpec BuildSpec(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        ExecutionJobDefinition? workload,
        string? requiredRuntimeProfile,
        GpResourceProfile resourceProfile)
    {
        if (workload == null)
        {
            // The no-registered-workload case (the default) is built through the shared
            // spec builder so the durable spec — parameter bag AND envelope — is
            // identical to the one the GP Devkit local runner produces for the same
            // plan (issue #2180). The per-job resource profile is NOT projected here: the
            // batch.* sizing keys are meaningless to the local/Kubernetes baseline and would
            // break the local-runner spec-parity invariant.
            return GeoprocessingSpecBuilder.BuildNoWorkloadSpec(plan, specParams, requiredRuntimeProfile);
        }

        // Project the plan's id / process-definitions / output kinds / step inputs onto
        // the workload-supplied parameter bag through the same shared projection.
        GeoprocessingSpecBuilder.ProjectPlanParameters(plan, specParams);

        // Project the per-job resource profile onto the batch.* params BEFORE merging the workload
        // defaults: set-if-absent semantics make explicit request params win over the per-job
        // profile, and the per-job profile win over the workload's baseline sizing.
        resourceProfile.ProjectOnto(specParams);

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
            // Carry the workload's required serving↔worker job-contract version onto the spec so the
            // dispatcher can gate submission against the target backend's supported version (ADR-0060 #3b).
            ContractVersion = workload.ContractVersion,
            Parameters = specParams
        };
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

        // A single execution job runs exactly one process (the dispatcher resolves
        // one process id from the spec), so a multi-step plan submitted directly
        // would silently execute only the first step and drop the rest. Reject it
        // rather than mislead: multi-step DAGs are executed by the workflow
        // orchestration engine, which decomposes the DAG into one single-step job
        // per node and submits those here. This keeps the direct submit path honest
        // for MCP/gRPC/OGC callers that author a plan by hand.
        if (plan.Steps.Count > 1)
        {
            throw new GeoprocessingValidationException(
                "Multi-step plans are not executable on the direct submit path: a job runs a single " +
                "process, so steps beyond the first would be silently dropped. Submit one process per " +
                "job, or use the workflow orchestration engine to execute a multi-step DAG.");
        }
    }

    private void EnsurePlanCatalogValid(AnalysisPlan plan)
    {
        var (violations, _) = ProcessPlanValidator.Validate(plan, _processCatalog, _analyticsLimits);
        if (violations.Count == 0)
        {
            return;
        }

        foreach (var v in violations.Where(v => v.Code == "UNKNOWN_PROCESS"))
        {
            GeoprocessingServiceLog.UnknownProcessReferenced(_logger, v.FieldPath ?? "", v.Message);
        }

        var first = violations[0];
        throw new GeoprocessingValidationException(
            $"Plan failed catalog validation: {first.Code} — {first.Message}");
    }

    /// <summary>
    /// Derives the distinct artifact kinds the plan's Geoprocess steps produce from the
    /// process catalog. Used by the dry-run path when the plan does not declare its own
    /// <see cref="AnalysisPlan.Outputs"/> so the estimate reflects real process outputs.
    /// </summary>
    private List<ArtifactKind> DeriveArtifactKinds(AnalysisPlan plan)
    {
        var kinds = new List<ArtifactKind>();
        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || string.IsNullOrWhiteSpace(step.ProcessId))
            {
                continue;
            }

            if (_processCatalog.GetProcess(step.ProcessId) is not { } definition)
            {
                continue;
            }

            foreach (var kind in definition.OutputArtifactKinds)
            {
                if (!kinds.Contains(kind))
                {
                    kinds.Add(kind);
                }
            }
        }

        return kinds;
    }

    /// <summary>
    /// Derives the observable side effects the plan would produce: durable writes from
    /// mutating processes (<see cref="ProcessExecutionTier.Mutating"/>) and sink steps.
    /// Read-only analytic plans return an empty set (before the no-estimate disclosure the
    /// dry-run path appends).
    /// </summary>
    private List<string> DeriveSideEffects(AnalysisPlan plan)
    {
        var sideEffects = new List<string>();
        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || string.IsNullOrWhiteSpace(step.ProcessId))
            {
                continue;
            }

            if (_processCatalog.GetProcess(step.ProcessId) is { ExecutionTier: ProcessExecutionTier.Mutating })
            {
                sideEffects.Add(
                    $"Step '{step.StepId}' runs mutating process '{step.ProcessId}', which writes durable state (requires elevated authorization/approval).");
            }
            else if (step.ProcessId.StartsWith("sink.", StringComparison.Ordinal))
            {
                sideEffects.Add(
                    $"Step '{step.StepId}' runs sink process '{step.ProcessId}', which writes output to an external destination.");
            }
        }

        return sideEffects;
    }

    private bool ContainsMutatingProcess(AnalysisPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || string.IsNullOrWhiteSpace(step.ProcessId))
            {
                continue;
            }

            if (_processCatalog.GetProcess(step.ProcessId) is { ExecutionTier: ProcessExecutionTier.Mutating })
            {
                return true;
            }
        }

        return false;
    }

    private IExecutionJobStore RequireJobStore()
        => _jobStore ?? throw new GeoprocessingStoreUnavailableException();

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

        // Hash the MemoryStream's internal buffer directly via the span overload instead
        // of buffer.ToArray(), which would copy the whole written payload just to hand it
        // to SHA256.HashData.
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))).ToLowerInvariant();
    }

    private static void EnsureMatchingIdempotentRequest(
        ExecutionJobRecord existing, string requestFingerprint, ClaimsPrincipal principal)
    {
        // Reject cross-principal replay: a different caller must not silently
        // receive another principal's job via an idempotency-key collision.
        var requestedBy = existing.Audit.RequestedBy;
        var callerName = ResolvePrincipalId(principal);
        if (!string.IsNullOrWhiteSpace(requestedBy)
            && !string.Equals(requestedBy, callerName, StringComparison.Ordinal))
        {
            throw new GeoprocessingIdempotencyConflictException(existing.OperationId);
        }

        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new GeoprocessingIdempotencyConflictException(existing.OperationId);
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
