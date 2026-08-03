// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
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
/// <see cref="GeoprocessingJobDispatcher"/> (admission/queue/workload/backend plus the
/// approval-lane proposal routing that owns the <see cref="IOperationGateway"/>),
/// <see cref="CustomCodeJobSubmissionGate"/> (custom-code token gate), and
/// <see cref="GeoprocessingJobArtifactService"/> (raster input + result-package artifacts) —
/// so the orchestration here stays focused while behavior is preserved exactly.
/// </remarks>
internal sealed class GeoprocessingJobService : IGeoprocessingJobService
{
    private const string RequestFingerprintVersionPrefix = "gp-v2:";
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
    /// delegating sub-services. The sub-services are registered in DI alongside this type; the
    /// approval-lane operation gateway is owned by the <see cref="GeoprocessingJobDispatcher"/>.
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
        IGeoprocessingRasterSourceResolver? rasterSourceResolver = null,
        IOperationGateway? operationGateway = null,
        IOperatorScopeAuthorizer? scopeAuthorizer = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IServiceScopeFactory? serviceScopeFactory = null,
        IRasterExecutionPlanner? rasterExecutionPlanner = null,
        IOptionsMonitor<RasterExecutionPlannerOptions>? rasterExecutionOptions = null,
        IOptionsMonitor<GpWorkloadPlacementOptions>? workloadPlacementOptions = null)
        : this(
            progressStore,
            cancellationNotifiers,
            processCatalog,
            new GeoprocessingJobAuthorizer(
                authEvaluator,
                approvalEvaluator,
                scopeAuthorizer ?? NullOperatorScopeAuthorizer.Instance,
                // Always composed (never null): the submit-time layer read gate must not be
                // skippable by construction path. A caller that omits BOTH the accessor and
                // the scope factory gets a gate with no evaluable authorization context,
                // which denies layer-sourced plans rather than waving them through.
                new GeoprocessingLayerAccessGuard(
                    httpContextAccessor ?? new HttpContextAccessor(), serviceScopeFactory, logger),
                logger),
            new GeoprocessingJobDispatcher(
                logger,
                executorOptions,
                progressStore,
                jobQueue,
                workloadRegistry,
                backends,
                admissionEvaluator,
                operationGateway,
                rasterExecutionPlanner,
                rasterExecutionOptions,
                workloadPlacementOptions),
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

    public async Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
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

        // Per-layer read authorization, evaluated against the REQUESTING principal at
        // workflow-authoring time (honua-server#3043 review). The reconcile tick later
        // submits each step under the synthesized orchestrator identity, so this is the
        // only point where the human who scheduled the workflow faces the layer gate —
        // the same reason the mutating-process tier is pre-checked here (#2798).
        //
        // The bound plan is RETURNED, not discarded: it carries the dataset-layer binding
        // this principal was authorized for, and the authoring surface has to persist that
        // with the durable workflow definition. The reconcile tick's SubmitJobAsync re-runs
        // this gate under the orchestrator identity, which carries the wildcard-granted
        // `admin` role — so if the binding did not travel with the plan, the tick would
        // simply re-authorize and re-stamp whatever layer the dataset points at then and the
        // requester's authorization would be moot. With the binding present, the gate
        // enforces it and a re-pointed dataset fails the step. Steps whose layerId/datasetId
        // are still unresolved workflow bindings do not resolve to a layer here and are
        // gated at submission instead.
        return await _authorizer.EnsureLayerReadAccessAsync(plan, principal, cancellationToken)
            .ConfigureAwait(false);
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

        var rasterExecutionViolation = GeoprocessingJobArtifactService.GetTypedRasterExecutionViolation(plan);
        if (rasterExecutionViolation is not null)
        {
            violations.Add(rasterExecutionViolation);
        }

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
        GeoprocessingJobArtifactService.EnsureTypedRasterExecutionSupported(plan);

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

    public Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
        => SubmitJobCoreAsync(plan, idempotencyKey, principal, protocolMetadata, resumingApproved: false, cancellationToken);

    /// <summary>
    /// Shared submit pipeline for both the caller-initiated submit path and the
    /// approval-resume path. When <paramref name="resumingApproved"/> is true the
    /// caller is the operation gateway replaying a proposal that already cleared the
    /// baseline execute, mutating-process, and approval gates at proposal-creation
    /// time; those gates are therefore bypassed here so the resumed submission is not
    /// re-denied against the reconstructed submitter principal (ADR-0064, #2814).
    /// Structural, executability, and catalog validation always run.
    /// </summary>
    private async Task<ExecutionJobRecord> SubmitJobCoreAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool resumingApproved,
        CancellationToken cancellationToken = default)
    {
        // Centralize submit-path authorization here so every adapter (GPServer,
        // OGC Processes, MCP, and the AnalysisContent run/rerun paths) is gated
        // through the shared pipeline rather than relying on caller discipline
        // (#2263). Adapters that already call EnsureCallerAuthorizedAsync before
        // submit stay correct — this evaluation is idempotent and never
        // double-fails an authorized caller.
        if (!resumingApproved)
        {
            await _authorizer.EnsureAuthorizedAsync(
                principal,
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                cancellationToken).ConfigureAwait(false);
        }

        ValidatePlanStructure(plan);
        EnsurePlanExecutable(plan);
        _artifacts.ValidateRasterSources(plan, cancellationToken);

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
            // The approval lane never persists a custom-code proposal, so resumingApproved
            // is always false here.
            if (!resumingApproved)
            {
                await _authorizer.EnsureAuthorizedAsync(
                    principal,
                    OperatorResourceType.Process,
                    OperatorOperation.ExecuteCustomCode,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            EnsurePlanCatalogValid(plan);
        }

        // RAST-003 defines and projects the v2 contract, but no current local or remote
        // worker consumes it safely. Refuse before approval proposals, fingerprints, job
        // records, or queue dispatch until #3090 introduces authenticated source resolution.
        GeoprocessingJobArtifactService.EnsureTypedRasterExecutionSupported(plan);

        // Evaluate the mutating-process tier unconditionally — including for custom-code
        // submissions, which skip catalog validation. Nothing else asserts that a custom-code
        // plan carries no executable mutating Geoprocess step, so running this for both
        // branches closes the gap where a mutating catalog step smuggled onto a custom-code
        // submission would never face the ExecuteMutatingProcess gate (#2798).
        if (!resumingApproved && ContainsMutatingProcess(plan))
        {
            await _authorizer.EnsureAuthorizedAsync(
                principal,
                OperatorResourceType.Process,
                OperatorOperation.ExecuteMutatingProcess,
                cancellationToken).ConfigureAwait(false);
        }

        // Per-LAYER read authorization for layer-sourced processes (#2283 review).
        // Process.Execute authorizes running a process; it does not authorize the specific
        // catalog layers that process will read. This is the only point in the job's life
        // where the submitter's real principal (roles, grants, tenant scope) is still in
        // hand — the durable record keeps only the submitter id — so the gate runs here
        // and a caller that cannot read a layer is refused at submission instead of being
        // handed a queued job that would read it. Skipped on the approval-resume path for
        // the same reason as the gates above: it already ran, against the live submitter,
        // when the proposal was created — and the persisted proposal plan already carries
        // the authorized-layer bindings the gate stamped before EnsureApprovedAsync parked it.
        //
        // The gate returns the plan with the authorized dataset layer bound to each gated
        // step; reassigning `plan` here is what carries that binding into the approval
        // proposal, the request fingerprint, and the durable job spec the executor reads,
        // so a dataset re-pointed while the job is queued cannot be read unauthorized.
        if (!resumingApproved)
        {
            plan = await _authorizer.EnsureLayerReadAccessAsync(plan, principal, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!resumingApproved)
        {
            await EnsureApprovedAsync(
                principal, plan, idempotencyKey, protocolMetadata, isCustomCode, cancellationToken)
                .ConfigureAwait(false);
        }

        var jobStore = RequireJobStore();
        var now = DateTimeOffset.UtcNow;
        var resolvedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        var jobId = CreateJobId(resolvedKey);
        var requestFingerprint = CreateRequestFingerprint(plan, protocolMetadata);
        var legacyRequestFingerprint = CreateLegacyRequestFingerprint(plan);

        if (resolvedKey is not null)
        {
            // Replays reuse the exact durable raster/compute decision that was admitted on the
            // first submission. Looking up the deterministic id before source materialization and
            // planning prevents a later health or policy change from refusing an already-created
            // request. The TryCreate race path below remains authoritative for concurrent callers.
            var existing = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureMatchingIdempotentRequest(
                    existing,
                    requestFingerprint,
                    legacyRequestFingerprint,
                    principal);
                EnsureSubmissionDidNotRollback(existing);
                GeoprocessingServiceLog.JobSubmittedIdempotent(_logger, jobId);
                return existing;
            }
        }

        // Resolve the legacy layerId/rasterId compatibility path before planning so the planner
        // sees the canonical inline source that the native worker will consume. The request
        // fingerprint above remains bound to the caller's stable catalog reference rather than
        // the materialized bytes. The planner itself still examines metadata/encoded length only;
        // it never decodes the raster or loads GDAL into the serving process.
        plan = await _artifacts.ResolveRasterSourcesAsync(plan, cancellationToken).ConfigureAwait(false);

        var rasterDefinition = !isCustomCode && plan.Steps[0].ProcessId is { } rasterProcessId
            ? _processCatalog.GetProcess(rasterProcessId)
            : null;
        var rasterDecision = await _dispatcher
            .PlanRasterExecutionAsync(plan, rasterDefinition, cancellationToken)
            .ConfigureAwait(false);

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

        if (!isCustomCode)
        {
            GpResourceProfile.RejectBackendResourceOverrides(specParams);
        }

        var partitionKey = ResolvePartitionKey(specParams);
        var costWeight = ResolveAdmissionCostWeight(plan, rasterDecision);
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

        // A custom-code job forces the custom-code runtime profile so the claim
        // fence routes it to the custom-code Batch workload (and away from the lean
        // dispatcher and the GDAL worker); otherwise stamp the catalog-required profile.
        var requiredRuntimeProfile = isCustomCode
            ? CustomCodeJobContract.RuntimeProfile
            : ResolveRequiredRuntimeProfile(plan, rasterDecision);
        // Per-job serverless sizing (#2165): the heaviest catalog-derived resource profile across
        // the plan's steps, overridden by any explicit gp.resource.* request values. Projected onto
        // the spec's batch.* params so AwsBatchComputeBackend.SubmitJob sizes vCPU/memory/timeout/
        // retry/GPU and selects the ephemeral job-def tier per job. Instant and terraform-free.
        var resourceProfile = ResolveResourceProfile(plan, specParams, isCustomCode);
        var placement = await _dispatcher
            .ResolveWorkloadAsync(
                isCustomCode,
                requiredRuntimeProfile,
                resourceProfile,
                specParams,
                cancellationToken,
                rasterDecision)
            .ConfigureAwait(false);
        if (rasterDecision?.Placement == RasterExecutionPlacement.RemoteBackend
            && placement.Workload is not null)
        {
            // Raster planning proves that remote native execution is required; workload
            // placement then chooses the compatible provider envelope. Finalize the durable
            // raster snapshot with that exact backend before the job record is persisted.
            rasterDecision = rasterDecision with { Backend = placement.Workload.Backend };
        }

        var spec = BuildSpec(
            plan,
            specParams,
            placement.Workload,
            requiredRuntimeProfile,
            resourceProfile,
            rasterDecision,
            placement.Decision);
        var queuedPhase = rasterDecision is null
            ? "Queued"
            : $"Queued: raster decision {rasterDecision.ReasonCode}";

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = queuedPhase,
            RetryPolicy = ResolveRetryPolicy(resourceProfile, placement.Workload),
            TimeoutPolicy = ResolveTimeoutPolicy(resourceProfile),
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
                EnsureMatchingIdempotentRequest(
                    existing,
                    requestFingerprint,
                    legacyRequestFingerprint,
                    principal);
                EnsureSubmissionDidNotRollback(existing);
                GeoprocessingServiceLog.JobSubmittedIdempotent(_logger, jobId);
                return existing;
            }

            throw new InvalidOperationException("Failed to create or locate execution job.");
        }

        try
        {
            var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, plan.PlanId, queuedPhase);
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

    private async Task EnsureApprovedAsync(
        ClaimsPrincipal principal,
        AnalysisPlan plan,
        string? idempotencyKey,
        IReadOnlyDictionary<string, string>? protocolMetadata,
        bool isCustomCode,
        CancellationToken cancellationToken)
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

        var policyRef = approval.PolicyRef ?? "unknown";
        GeoprocessingServiceLog.SubmitRejectedApprovalRequired(_logger, policyRef);

        // Park the gated plan on the approval lane. The dispatcher owns the durable
        // proposal/gateway surface (ADR-0064, #2814): when it is available and the
        // submission is not custom code it persists an AwaitingApproval proposal so the
        // plan is resumable via honua://proposals/{id}, then throws carrying the proposal
        // id; otherwise (custom code, or no gateway on lightweight/Redis-free hosts) it
        // hard-fails without a proposal id. This call always throws.
        await _dispatcher.CreateApprovalProposalOrThrowAsync(
                policyRef,
                plan,
                idempotencyKey,
                ResolvePrincipalId(principal),
                protocolMetadata,
                isCustomCode,
                approvalGatedProcessId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ExecutionJobRecord> ResumeApprovedJobAsync(
        GeoprocessExecutionPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payload.Plan);

        // The approval and mutating-process gates were satisfied when the proposal
        // was created; re-run the submission with those gates bypassed, attributing
        // the job to the original submitter recorded in the payload. A synthetic
        // principal carrying only the submitter identity preserves job ownership and
        // partition-scoped admission without re-deriving the submitter's roles.
        var principal = BuildResumePrincipal(payload.RequestedBy);
        return await SubmitJobCoreAsync(
                payload.Plan,
                payload.IdempotencyKey,
                principal,
                payload.Metadata,
                resumingApproved: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ClaimsPrincipal BuildResumePrincipal(string? requestedBy)
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(requestedBy))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, requestedBy));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "GeoprocessingApprovalResume"));
    }

    private static double ResolveAdmissionCostWeight(
        AnalysisPlan plan,
        RasterExecutionDecision? rasterDecision)
    {
        if (rasterDecision is null)
        {
            return Math.Max(plan.Steps.Count, 1);
        }

        if (rasterDecision.Placement == RasterExecutionPlacement.RemoteBackend)
        {
            // The partition cost ledger protects compute owned by the local serving/worker
            // plane. Remote native isolation still consumes orchestration capacity, but its
            // decoded/scratch footprint is borne by the selected batch substrate. Charging
            // that remote footprint here would make every conservative or large offload exceed
            // the default local admission ceiling before it could reach the remote backend.
            return Math.Max(plan.Steps.Count, 1);
        }

        if (rasterDecision.Cost.UsesConservativeValues)
        {
            return 1_000d;
        }

        var decodedWeight = rasterDecision.Cost.DecodedBytes / (64d * 1024d * 1024d);
        var scratchWeight = rasterDecision.Cost.ExpectedScratchBytes / (128d * 1024d * 1024d);
        var databaseWeight = rasterDecision.Cost.ExpectedDatabaseWork / 10_000_000d;
        return Math.Clamp(decodedWeight + scratchWeight + databaseWeight, 1d, 1_000d);
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
    private string? ResolveRequiredRuntimeProfile(
        AnalysisPlan plan,
        RasterExecutionDecision? rasterDecision = null)
    {
        if (rasterDecision is not null)
        {
            return rasterDecision.Engine == RasterEngine.Postgis
                ? RuntimeProfiles.RasterPostgis
                : RuntimeProfiles.Native;
        }

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

    private static JobRetryPolicy? ResolveRetryPolicy(
        GpResourceProfile resourceProfile,
        ExecutionJobDefinition? workload)
    {
        if (resourceProfile.RetryAttempts is not { } totalAttempts)
        {
            return null;
        }

        // AWS Batch and Azure Batch receive the canonical total-attempt budget through their
        // provider submission contracts. Disable the shared reconciler retry layer for those
        // jobs so provider attempts are not multiplied by a second series of submissions.
        if (workload?.TargetKind is BatchComputeTargetKind.AwsBatch or BatchComputeTargetKind.AzureBatch)
        {
            return JobRetryPolicy.None;
        }

        return JobRetryPolicy.Default with { MaxAttempts = totalAttempts };
    }

    private static JobTimeoutPolicy? ResolveTimeoutPolicy(GpResourceProfile resourceProfile)
        => resourceProfile.TimeoutSeconds is { } timeoutSeconds
            ? JobTimeoutPolicy.Default with { MaxDuration = TimeSpan.FromSeconds(timeoutSeconds) }
            : null;

    private static ExecutionJobSpec BuildSpec(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        ExecutionJobDefinition? workload,
        string? requiredRuntimeProfile,
        GpResourceProfile resourceProfile,
        RasterExecutionDecision? rasterDecision = null,
        ExecutionPlacementDecision? placementDecision = null)
    {
        if (workload == null)
        {
            // The no-registered-workload case (the default) is built through the shared
            // spec builder so the durable spec — parameter bag AND envelope — is
            // identical to the one the GP Devkit local runner produces for the same
            // plan (issue #2180). The per-job resource profile is NOT projected here: the
            // batch.* sizing keys are meaningless to the local/Kubernetes baseline and would
            // break the local-runner spec-parity invariant.
            return GeoprocessingSpecBuilder.BuildNoWorkloadSpec(
                plan,
                specParams,
                requiredRuntimeProfile,
                rasterDecision,
                placementDecision);
        }

        // Project the plan's id / process-definitions / output kinds / step inputs onto
        // the workload-supplied parameter bag through the same shared projection.
        GeoprocessingSpecBuilder.ProjectPlanParameters(plan, specParams);

        // Project the per-job resource profile onto the batch.* params BEFORE merging the workload
        // defaults: set-if-absent semantics make explicit request params win over the per-job
        // profile, and the per-job profile win over the workload's baseline sizing.
        if (!string.Equals(workload.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
        {
            resourceProfile.ProjectOnto(specParams, workload.TargetKind);
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
            // Carry the workload's required serving↔worker job-contract version onto the spec so the
            // dispatcher can gate submission against the target backend's supported version (ADR-0060 #3b).
            ContractVersion = GeoprocessingSpecBuilder.ResolveRequiredContractVersion(
                plan,
                workload.ContractVersion),
            RasterExecution = rasterDecision,
            ComputePlacement = placementDecision,
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

            // Not a .Where(...) candidate: the filter predicate (!kinds.Contains(kind))
            // checks against `kinds` itself as it grows across iterations, so this is a
            // dedup-while-building accumulator rather than a pure filter over the input.
            foreach (var kind in (definition.OutputArtifactKinds).Where(kind => !kinds.Contains(kind)))
            {
                kinds.Add(kind);
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

    internal static string CreateRequestFingerprint(
        AnalysisPlan plan,
        IReadOnlyDictionary<string, string>? requestParameters = null)
    {
        var executionParameters = ResolveFingerprintExecutionParameters(requestParameters);
        return RequestFingerprintVersionPrefix + CreateRequestFingerprintCore(plan, executionParameters);
    }

    internal static string CreateLegacyRequestFingerprint(AnalysisPlan plan)
        => CreateRequestFingerprintCore(plan, []);

    private static string CreateRequestFingerprintCore(
        AnalysisPlan plan,
        List<KeyValuePair<string, string>> executionParameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("planId", plan.PlanId);
            writer.WriteString("intentId", plan.IntentId);

            // Preserve the legacy plan-only byte shape when no placement/resource behavior was
            // requested. When present, these values affect durable execution selection and must
            // participate in idempotency rather than silently reusing a differently placed job.
            if (executionParameters.Count > 0)
            {
                writer.WriteStartArray("executionParameters");
                foreach (var parameter in executionParameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Key", parameter.Key);
                    writer.WriteString("Value", parameter.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

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

                // Preserve the pre-raster fingerprint byte shape for legacy plans. This is a
                // rolling-deployment contract: jobs submitted before typed raster bindings existed
                // omitted the property, so emitting an empty array would make a retry conflict with
                // its already-durable idempotency record.
                if (step.RasterSources.Count > 0)
                {
                    writer.WriteStartArray("rasterSources");
                    foreach (var source in step.RasterSources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("Key", source.Key);
                        writer.WriteString("Descriptor", RasterSourceJson.Serialize(source.Value));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

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

    private static List<KeyValuePair<string, string>> ResolveFingerprintExecutionParameters(
        IReadOnlyDictionary<string, string>? requestParameters)
    {
        if (requestParameters is null)
        {
            return new List<KeyValuePair<string, string>>();
        }

        string[] keys =
        [
            GpWorkloadPlacementParameterKeys.Mode,
            GpWorkloadPlacementParameterKeys.Backend,
            GpWorkloadPlacementParameterKeys.Affinity,
            GpResourceProfile.VcpusRequestKey,
            GpResourceProfile.MemoryMibRequestKey,
            GpResourceProfile.GpuCountRequestKey,
            GpResourceProfile.TimeoutSecondsRequestKey,
            GpResourceProfile.RetryAttemptsRequestKey,
            GpResourceProfile.EphemeralGibRequestKey,
            GpResourceProfile.ArchRequestKey,
        ];
        var result = new List<KeyValuePair<string, string>>(keys.Length);
        foreach (var key in keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (requestParameters.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                result.Add(new KeyValuePair<string, string>(key, value.Trim()));
            }
        }

        return result;
    }

    private static void EnsureMatchingIdempotentRequest(
        ExecutionJobRecord existing,
        string requestFingerprint,
        string legacyRequestFingerprint,
        ClaimsPrincipal principal)
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

        // Pre-upgrade GP records carry the unversioned, plan-only digest. Accept the same
        // logical replay even when resource/placement inputs are present now; new records are
        // explicitly versioned, so changing those inputs still produces an idempotency conflict.
        if (!string.IsNullOrWhiteSpace(existingFingerprint)
            && !existingFingerprint.StartsWith(RequestFingerprintVersionPrefix, StringComparison.Ordinal)
            && string.Equals(existingFingerprint, legacyRequestFingerprint, StringComparison.Ordinal))
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
