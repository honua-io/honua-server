// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Geoprocessing;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Durable workflow orchestration engine. Composes canonical analysis-plan jobs into
/// declarative DAG runs and reconciles each run's state against the underlying
/// <see cref="IWorkflowJobExecutor"/> substrate. Mirrors the lease-based reconcile
/// pattern established for deploy workflows.
/// </summary>
internal sealed class WorkflowOrchestrationEngine : IWorkflowCancellationCoordinator
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CancelLeaseRetryInterval = TimeSpan.FromMilliseconds(100);
    private const int CancelLeaseMaxAttempts = 30;

    private readonly IWorkflowRunStore _runStore;
    private readonly IWorkflowDefinitionStore _definitionStore;
    private readonly IWorkflowJobExecutor _jobService;
    private readonly IUniversalProgressStore _progressStore;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkflowOrchestrationEngine> _logger;
    private readonly RbacOptions _rbacOptions;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IPrincipalMembershipSource? _principalMembershipSource;
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public WorkflowOrchestrationEngine(
        IWorkflowRunStore runStore,
        IWorkflowDefinitionStore definitionStore,
        IWorkflowJobExecutor jobService,
        IUniversalProgressStore progressStore,
        TimeProvider clock,
        ILogger<WorkflowOrchestrationEngine> logger,
        IOptions<RbacOptions>? rbacOptions = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IPrincipalMembershipSource? principalMembershipSource = null)
    {
        _rbacOptions = rbacOptions?.Value ?? new RbacOptions();
        _runStore = runStore;
        _definitionStore = definitionStore;
        _jobService = jobService;
        _progressStore = progressStore;
        _clock = clock;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _principalMembershipSource = principalMembershipSource;
    }

    /// <summary>
    /// Resolves the row/field security snapshot to pin on a run.
    /// </summary>
    /// <remarks>
    /// A manual run HAS a requesting principal, so the snapshot is captured from it. A cron or
    /// event-triggered run does not: the scheduler and event-trigger services call in under the
    /// synthesized orchestrator identity carrying <c>role=admin</c>, so capturing from it would
    /// hand every step job ADMIN row and field visibility — a restricted author's scheduled
    /// workflow would read all rows and unmasked fields on every tick. Those runs inherit the
    /// snapshot captured from the author when the workflow was published, then replace its role
    /// claims from the current membership source when that source manages the author (#3081).
    /// <para>
    /// A triggered run whose definition predates that field is REFUSED rather than falling back
    /// to the orchestrator capture, matching the submit path's treatment of an absent inherited
    /// snapshot: the fallback is a privilege escalation, and the resulting non-null snapshot
    /// would sail past the fail-closed guards at the read seam. Republishing the workflow
    /// captures the author and clears the refusal (honua-server#3068 review).
    /// </para>
    /// </remarks>
    private async Task<JobSecurityContextMembershipResult> ResolveRunSecurityContextAsync(
        WorkflowDefinition definition,
        WorkflowTriggerKind triggerKind,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (triggerKind == WorkflowTriggerKind.Manual)
        {
            var tenantContext = _httpContextAccessor?.HttpContext?.RequestServices
                .GetService<ITenantContext>();
            return new JobSecurityContextMembershipResult(
                JobSecurityContextCapture.Capture(principal, _rbacOptions, tenantContext),
                JobSecurityContextMembershipStatus.Current);
        }

        // WorkflowDefinitionValidationException, not a generic one: both trigger loops classify
        // an unrecognized exception as TRANSIENT and release the claim without advancing the
        // cursor, so a legacy definition would reclaim the same occurrence and log a failure on
        // every poll, forever. This is a permanent property of the stored definition — only
        // republishing changes it — and that is the exception both loops already treat as
        // permanent (honua-server#3068 review).
        var snapshot = definition.AuthorSecurityContext ?? throw new WorkflowDefinitionValidationException(
                $"Workflow '{definition.WorkflowId}' was published before its author's row/field "
                + "security identity was recorded, so a triggered run cannot reconstruct it. "
                + "Republish the workflow to enable scheduled and event-triggered runs.");
        var result = await JobSecurityContextCapture
            .RevalidateRoleMembershipAsync(snapshot, _principalMembershipSource, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == JobSecurityContextMembershipStatus.SnapshotFallback)
        {
            OrchestrationLog.AuthorMembershipSnapshotFallback(
                _logger,
                definition.WorkflowId,
                snapshot.PrincipalId ?? "unknown");
        }
        else if (result.Status == JobSecurityContextMembershipStatus.Changed)
        {
            OrchestrationLog.AuthorMembershipRevalidated(
                _logger,
                definition.WorkflowId,
                snapshot.PrincipalId ?? "unknown");
        }
        else if (result.Status == JobSecurityContextMembershipStatus.Inactive)
        {
            OrchestrationLog.AuthorMembershipInactive(
                _logger,
                definition.WorkflowId,
                snapshot.PrincipalId ?? "unknown");
            throw new WorkflowDefinitionValidationException(
                $"Workflow '{definition.WorkflowId}' author '{snapshot.PrincipalId ?? "unknown"}' "
                + "is no longer active; this triggered firing was refused.");
        }

        return result;
    }

    /// <summary>
    /// Input keys whose value names a catalog object the submit-time layer gate authorizes.
    /// Deliberately a name match rather than a catalog lookup: the check must hold for any
    /// process, including ones added later, and over-matching only ever refuses more.
    /// </summary>
    private static bool IsCatalogIdentifierInput(string key)
        => key.EndsWith("layerId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("datasetId", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a background-triggered workflow whose catalog identifiers are supplied through a
    /// ForEach placeholder.
    /// </summary>
    /// <remarks>
    /// Permanent by construction — it is a property of the stored definition — so it throws the
    /// exception both trigger loops treat as permanent rather than spinning the occurrence.
    /// Manual runs are unaffected: they carry a real principal, so run-creation authorization of
    /// the expanded plan IS the requester's decision. Lifting this needs publisher-derived
    /// authority at expansion time, which the definition does not currently carry
    /// (honua-server#3043 review).
    /// </remarks>
    private static void RejectDynamicCatalogIdentifiers(WorkflowDefinition definition)
    {
        foreach (var step in definition.Steps.Where(step => step.ForEach is not null))
        {
            var placeholder = step.ForEach!.ItemPlaceholder;
            foreach (var planStep in step.Plan.Steps)
            {
                var dynamicInput = planStep.Inputs.FirstOrDefault(input =>
                    IsCatalogIdentifierInput(input.Key)
                    && input.Value.Contains(placeholder, StringComparison.Ordinal));

                if (dynamicInput.Key is not null)
                {
                    throw new WorkflowDefinitionValidationException(
                        $"Step '{step.StepId}' supplies the catalog identifier '{dynamicInput.Key}' from a "
                        + "ForEach item, which a background trigger cannot authorize: publication resolves "
                        + "the publisher's access but cannot see the item value, and authorizing it at run "
                        + "creation would use the orchestrator's admin authority. Use a static identifier, "
                        + "or run this workflow manually.");
                }
            }
        }
    }

    public async Task<WorkflowRun> CreateRunAsync(
        WorkflowDefinition definition,
        WorkflowTriggerKind triggerKind,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(principal);

        var failures = WorkflowDefinitionValidator.Validate(definition);
        if (failures.Count > 0)
        {
            throw new WorkflowDefinitionValidationException(failures);
        }

        // Unroll ForEach steps deterministically so the run's durable step states match
        // the flat DAG the reconciler will rebuild from the stored definition each tick.
        var expanded = WorkflowDefinitionExpander.Expand(definition);

        // Resolved before the gate loop below because that loop authorizes against it on the
        // triggered paths. Ordering is otherwise unchanged: this throws only for a legacy
        // definition, the same permanent failure both trigger loops already classify, and
        // nothing has been persisted yet either way.
        var membershipResult = await ResolveRunSecurityContextAsync(
                definition, triggerKind, principal, cancellationToken)
            .ConfigureAwait(false);
        var runSecurityContext = membershipResult.Context;

        // #2798/#3046: gate the mutating-process execution tier AND per-layer read access
        // BEFORE any step job is submitted. The reconcile loop submits step jobs under a
        // synthesized orchestrator principal that carries role=admin and so bypasses the
        // operator evaluator; without this pre-check an Execute-only operator could schedule
        // a workflow whose compiled steps import, mutate, or write durable sinks and have
        // every step execute without anyone facing the gates. The check runs before the run
        // is persisted so a denial leaves no orphaned run state.
        //
        // A background firing has no requesting human. Reject dynamic catalog identifiers that
        // publication could not bind, then re-authorize every expanded plan against the AUTHOR
        // restored from the durable snapshot rather than the scheduler's admin identity. Manual
        // runs authorize the live requester and retain any pins produced after expansion.
        if (triggerKind != WorkflowTriggerKind.Manual)
        {
            RejectDynamicCatalogIdentifiers(definition);
        }

        var authorizationPrincipal = triggerKind == WorkflowTriggerKind.Manual
            ? principal
            : JobSecurityContextCapture.Restore(runSecurityContext);

        var authorizedPlans = new Dictionary<string, AnalysisPlan>(StringComparer.Ordinal);
        try
        {
            foreach (var step in expanded.Steps)
            {
                authorizedPlans[step.StepId] = await _jobService
                    .EnsurePlanExecutionAuthorizedAsync(step.Plan, authorizationPrincipal, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
            when (membershipResult.HasRemovedRoles
                  && ex is UnauthorizedAccessException or GeoprocessingAuthorizationException)
        {
            OrchestrationLog.AuthorMembershipNoLongerAuthorizes(
                _logger,
                definition.WorkflowId,
                runSecurityContext.PrincipalId ?? "unknown");
            throw new WorkflowDefinitionValidationException(
                $"Workflow '{definition.WorkflowId}' author '{runSecurityContext.PrincipalId ?? "unknown"}' "
                + "lost role membership required by this triggered firing; the firing was refused.");
        }

        var now = _clock.GetUtcNow();
        var runId = $"wf-{Guid.NewGuid():N}";
        var stepStates = expanded.Steps
            .Select(step => new WorkflowStepState
            {
                StepId = step.StepId,
                PlanId = step.Plan.PlanId,
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                AuthorizedPlan = authorizedPlans.GetValueOrDefault(step.StepId)
            })
            .ToArray();

        var run = new WorkflowRun
        {
            RunId = runId,
            WorkflowId = definition.WorkflowId,
            Status = WorkflowRunStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            StepStates = stepStates,
            TriggerKind = triggerKind,
            Metadata = metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            Audit = new OperationAuditInfo
            {
                RequestedBy = principal.Identity?.Name,
                // Pin the RUN REQUESTER's row/field security identity (#3068). Each step job is
                // later submitted under OrchestrationSystemPrincipal, which carries role=admin so
                // the reconcile loop can dispatch; capturing the snapshot from THAT principal
                // would give every step job admin row/field visibility and launder away the
                // requester's RLS predicate and field mask. Captured here, where the real
                // principal is still in hand, and replayed on every step submission.
                SubmitterSecurityContext = runSecurityContext
            }
        };

        var created = await _runStore.TryCreateAsync(run, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException($"Failed to persist workflow run '{runId}'.");
        }

        try
        {
            var progress = WorkflowProgress.CreateForPendingRun(runId, definition.WorkflowId, stepStates.Length, now);
            await _progressStore.SetProgressAsync(runId, progress, ProgressRetention, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrchestrationLog.ProgressProjectionFailed(_logger, runId, ex);
        }

        OrchestrationTelemetry.RunsCreated.Add(
            1,
            new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.TriggerKind, triggerKind.ToString()),
            new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.WorkflowId, definition.WorkflowId));

        OrchestrationLog.WorkflowRunCreated(_logger, runId, definition.WorkflowId, triggerKind.ToString());
        return run;
    }

    /// <summary>
    /// Attempts to mark a workflow run as cancelled and synchronise the progress projection.
    /// The reconcile loop picks up the cancelled status on its next tick and cascades child
    /// job cancellation. Returns <see cref="WorkflowCancellationOutcome"/> describing the
    /// resulting state so callers (e.g., the admin endpoint) can shape their responses.
    /// </summary>
    public async Task<WorkflowCancellationOutcome> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return WorkflowCancellationOutcome.NotFound;
        }

        // Take the reconcile lease before read-modify-write. `IWorkflowRunStore.SetAsync`
        // is unconditional last-write-wins, so without serialising against the reconcile
        // loop's writes a concurrent reconcile could clobber the Cancelled status back to
        // Running or Succeeded after this method has already returned success to the
        // caller. Bounded polling absorbs transient contention without blocking the admin
        // request path indefinitely; if the lease cannot be acquired within the window the
        // caller may safely retry.
        var leaseAcquired = false;
        for (var attempt = 0; attempt < CancelLeaseMaxAttempts && !leaseAcquired; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            leaseAcquired = await _runStore
                .TryAcquireLeaseAsync(runId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false);
            if (!leaseAcquired)
            {
                await Task.Delay(CancelLeaseRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!leaseAcquired)
        {
            // A reconcile is actively holding the lease. Surface this as a retriable
            // outcome so the admin endpoint translates it into a shaped 409 rather than
            // leaking a 500. The caller can safely retry the same cancel request; the
            // run state has not been mutated by this attempt.
            OrchestrationLog.WorkflowCancelLeaseContention(_logger, runId);
            return WorkflowCancellationOutcome.LeaseContention;
        }

        try
        {
            var run = await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return WorkflowCancellationOutcome.NotFound;
            }

            if (IsRunTerminal(run.Status))
            {
                return run.Status == WorkflowRunStatus.Cancelled
                    ? WorkflowCancellationOutcome.AlreadyCancelled
                    : WorkflowCancellationOutcome.AlreadyTerminal;
            }

            var now = _clock.GetUtcNow();
            var cancelled = run with
            {
                Status = WorkflowRunStatus.Cancelled,
                UpdatedAt = now
            };

            await PersistRunAsync(cancelled, cancellationToken).ConfigureAwait(false);
            return WorkflowCancellationOutcome.CancellationRequested;
        }
        finally
        {
            await _runStore.ReleaseLeaseAsync(runId, _ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task ReconcileWorkflowRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        var leaseAcquired = await _runStore.TryAcquireLeaseAsync(runId, _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewLeaseUntilCancelledAsync(runId, reconciliationCancellation);

        using var activity = OrchestrationTelemetry.StartReconcileRunActivity(runId, string.Empty, 0);

        try
        {
            var run = await _runStore.GetAsync(runId, reconciliationCancellation.Token).ConfigureAwait(false);
            if (run is null)
            {
                return;
            }

            // A Cancelled run may still own queued or running child jobs that require cascade
            // cleanup. Only skip reconcile when every step has also reached a terminal state.
            if (IsRunTerminal(run.Status) && run.StepStates.All(s => IsStepTerminal(s.Status)))
            {
                return;
            }

            activity?.SetTag(OrchestrationTelemetry.Tags.WorkflowId, run.WorkflowId);
            activity?.SetTag("honua.orchestration.step_count", run.StepStates.Count);

            var rawDefinition = await _definitionStore.GetAsync(run.WorkflowId, reconciliationCancellation.Token).ConfigureAwait(false);
            // Re-apply the deterministic ForEach unroll so the reconciler sees the same
            // flat step-set the run's durable states were created from. The transform is
            // pure, so both sides agree and the step-set consistency guard holds.
            var definition = rawDefinition is null ? null : WorkflowDefinitionExpander.Expand(rawDefinition);
            if (definition is null)
            {
                var now = _clock.GetUtcNow();
                var principal = OrchestrationSystemPrincipal.Create(run.Audit.RequestedBy);
                var warnings = new List<string>(run.Warnings);

                var finalisedSteps = run.StepStates.ToArray();
                for (var i = 0; i < finalisedSteps.Length; i++)
                {
                    var s = finalisedSteps[i];
                    if (IsStepTerminal(s.Status))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(s.JobId) &&
                        s.Status is WorkflowStepStatus.Queued or WorkflowStepStatus.Running)
                    {
                        try
                        {
                            await _jobService.CancelJobAsync(s.JobId!, principal, reconciliationCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (reconciliationCancellation.Token.IsCancellationRequested)
                        {
                            throw;
                        }
                        // Intentional catch-all: this is a per-step loop cancelling orphaned jobs
                        // when the workflow definition is missing; one step's job-cancel failure
                        // must not abort finalisation of the remaining steps in the run.
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            OrchestrationLog.WorkflowStepCancelJobFailed(_logger, run.RunId, s.StepId, s.JobId!, ex);
                            AddOrReplaceCancelWarning(warnings, s.StepId, s.JobId!, "Job cancellation failed.");
                            continue;
                        }
                    }

                    finalisedSteps[i] = s with
                    {
                        Status = WorkflowStepStatus.Cancelled,
                        CompletedAt = now,
                        ErrorMessage = s.ErrorMessage ?? "Workflow definition missing; step was not executed."
                    };
                }

                var allStepsTerminal = finalisedSteps.All(s => IsStepTerminal(s.Status));
                var runStatus = IsRunTerminal(run.Status) ? run.Status
                    : allStepsTerminal ? WorkflowRunStatus.Failed
                    : run.Status;
                var errorMessage = IsRunTerminal(run.Status)
                    ? run.ErrorMessage
                    : allStepsTerminal
                        ? $"Workflow definition '{run.WorkflowId}' was not found."
                        : run.ErrorMessage;
                var finalised = run with
                {
                    Status = runStatus,
                    UpdatedAt = now,
                    CompletedAt = allStepsTerminal ? (run.CompletedAt ?? now) : run.CompletedAt,
                    ErrorMessage = errorMessage,
                    StepStates = finalisedSteps,
                    Warnings = warnings
                };
                await PersistRunAsync(finalised, reconciliationCancellation.Token).ConfigureAwait(false);
                return;
            }

            var updated = await ReconcileRunAsync(run, definition, reconciliationCancellation.Token).ConfigureAwait(false);
            if (!Equals(updated, run))
            {
                await PersistRunAsync(updated, reconciliationCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            OrchestrationLog.ReconciliationLeaseLost(_logger, runId);
            return;
        }
        catch (Exception ex)
        {
            OrchestrationLog.ReconciliationFailed(_logger, runId, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            reconciliationCancellation.Cancel();
            try
            {
                await renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
                // Expected: the renewal loop observes the cancellation we just requested above.
            }

            await _runStore.ReleaseLeaseAsync(runId, _ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<WorkflowRun> ReconcileRunAsync(
        WorkflowRun run,
        WorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        var definitionById = definition.Steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        var states = run.StepStates.ToDictionary(state => state.StepId, StringComparer.Ordinal);
        var now = _clock.GetUtcNow();

        if (states.Count != definitionById.Count || states.Keys.Any(k => !definitionById.ContainsKey(k)))
        {
            var runStepIds = new HashSet<string>(states.Keys, StringComparer.Ordinal);
            var defStepIds = new HashSet<string>(definitionById.Keys, StringComparer.Ordinal);
            var detail = $"Added: [{string.Join(", ", defStepIds.Except(runStepIds))}], Removed: [{string.Join(", ", runStepIds.Except(defStepIds))}]";
            OrchestrationLog.DefinitionStepSetMismatch(_logger, run.RunId, run.WorkflowId, detail);

            var principal = OrchestrationSystemPrincipal.Create(run.Audit.RequestedBy);
            var mismatchWarnings = new List<string>(run.Warnings);
            var finalisedSteps = run.StepStates.ToArray();
            for (var i = 0; i < finalisedSteps.Length; i++)
            {
                var s = finalisedSteps[i];
                if (IsStepTerminal(s.Status))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(s.JobId) &&
                    s.Status is WorkflowStepStatus.Queued or WorkflowStepStatus.Running)
                {
                    try
                    {
                        await _jobService.CancelJobAsync(s.JobId!, principal, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    // Intentional catch-all: this is a per-step loop cancelling orphaned jobs
                    // after a step-set mismatch between the run and its definition; one step's
                    // job-cancel failure must not abort finalisation of the remaining steps.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        OrchestrationLog.WorkflowStepCancelJobFailed(_logger, run.RunId, s.StepId, s.JobId!, ex);
                        AddOrReplaceCancelWarning(mismatchWarnings, s.StepId, s.JobId!, "Job cancellation failed.");
                        continue;
                    }
                }

                finalisedSteps[i] = s with
                {
                    Status = WorkflowStepStatus.Cancelled,
                    CompletedAt = now,
                    ErrorMessage = s.ErrorMessage ?? "Definition step-set changed since run creation."
                };
            }

            var mismatchAllTerminal = finalisedSteps.All(s => IsStepTerminal(s.Status));
            var mismatchStatus = IsRunTerminal(run.Status) ? run.Status
                : mismatchAllTerminal ? WorkflowRunStatus.Failed
                : run.Status;
            var mismatchError = IsRunTerminal(run.Status)
                ? run.ErrorMessage
                : mismatchAllTerminal
                    ? $"Workflow definition '{run.WorkflowId}' step-set changed since run was created. {detail}"
                    : run.ErrorMessage;
            return run with
            {
                Status = mismatchStatus,
                StepStates = finalisedSteps,
                UpdatedAt = now,
                CompletedAt = mismatchAllTerminal ? (run.CompletedAt ?? now) : run.CompletedAt,
                ErrorMessage = mismatchError,
                Warnings = mismatchWarnings
            };
        }

        var warnings = new List<string>(run.Warnings);
        var changed = false;

        foreach (var definitionStep in definition.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!states.TryGetValue(definitionStep.StepId, out var state))
            {
                continue;
            }

            if (IsStepTerminal(state.Status))
            {
                continue;
            }

            // Cascade cancellation if the run is already cancelled (the admin cancel path or
            // the background service marks it). Before finalising the step, signal the
            // underlying substrate so worker-owned jobs do not keep running past the parent
            // workflow's terminal state.
            if (run.Status == WorkflowRunStatus.Cancelled && state.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.Queued or WorkflowStepStatus.Running)
            {
                if (!string.IsNullOrWhiteSpace(state.JobId) &&
                    state.Status is WorkflowStepStatus.Queued or WorkflowStepStatus.Running)
                {
                    var principal = OrchestrationSystemPrincipal.Create(run.Audit.RequestedBy);
                    try
                    {
                        await _jobService.CancelJobAsync(state.JobId!, principal, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // Intentional catch-all: this is a per-step loop cascading cancellation to
                    // worker-owned jobs after the parent run was cancelled; one step's job-cancel
                    // failure must not abort finalisation of the remaining steps.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        OrchestrationLog.WorkflowStepCancelJobFailed(_logger, run.RunId, state.StepId, state.JobId!, ex);
                        AddOrReplaceCancelWarning(warnings, state.StepId, state.JobId!, "Job cancellation failed.");
                        changed = true;
                        continue;
                    }
                }

                states[state.StepId] = state with
                {
                    Status = WorkflowStepStatus.Cancelled,
                    CompletedAt = now
                };
                changed = true;
                continue;
            }

            switch (state.Status)
            {
                case WorkflowStepStatus.Pending:
                    {
                        if (!AreDependenciesSatisfied(definitionStep, definitionById, states, out var cascadePolicy))
                        {
                            if (cascadePolicy is { } cascadeReason)
                            {
                                states[state.StepId] = state with
                                {
                                    Status = cascadeReason.Status,
                                    CompletedAt = now,
                                    ErrorMessage = cascadeReason.Reason
                                };
                                OrchestrationLog.WorkflowStepSkipped(_logger, run.RunId, state.StepId, cascadeReason.Reason);
                                changed = true;
                            }

                            continue;
                        }

                        if (state.NextAttemptAt is { } nextAttempt && nextAttempt > now)
                        {
                            continue;
                        }

                        // Conditional branch: once dependencies are terminal, evaluate the
                        // step's predicate over prior step outputs. A not-taken branch is
                        // skipped (and reported as such); dependents that consume its output
                        // cascade-skip through the existing skip machinery.
                        if (definitionStep.Condition is { } condition &&
                            !WorkflowStepConditionEvaluator.Evaluate(condition, states))
                        {
                            states[state.StepId] = state with
                            {
                                Status = WorkflowStepStatus.Skipped,
                                CompletedAt = now,
                                ErrorMessage = $"Branch condition over '{condition.SourceStepId}' was not met."
                            };
                            OrchestrationLog.WorkflowStepSkipped(_logger, run.RunId, state.StepId, "branch condition not met");
                            changed = true;
                            continue;
                        }

                        var submission = await SubmitStepAsync(run, definition, definitionStep, state, states, warnings, cancellationToken).ConfigureAwait(false);
                        if (submission.NewState is not null)
                        {
                            states[state.StepId] = submission.NewState;
                            changed = true;
                        }

                        break;
                    }

                case WorkflowStepStatus.Queued:
                case WorkflowStepStatus.Running:
                    {
                        var warningsBefore = warnings.Count;
                        var observation = await ObserveStepAsync(run, definition, definitionStep, state, warnings, cancellationToken).ConfigureAwait(false);
                        if (observation is not null)
                        {
                            states[state.StepId] = observation;
                            changed = true;
                        }
                        else if (warnings.Count > warningsBefore)
                        {
                            changed = true;
                        }

                        break;
                    }
            }
        }

        // Post-pass: materialise run status from step statuses.
        var stepStatesOrdered = definition.Steps
            .Select(s => states[s.StepId])
            .ToArray();

        var runStatus = DeriveRunStatus(stepStatesOrdered, run.Status);
        var allStepsTerminal = stepStatesOrdered.All(s => IsStepTerminal(s.Status));
        var completedAt = IsRunTerminal(runStatus) && allStepsTerminal && run.CompletedAt is null ? now : run.CompletedAt;
        string? errorMessage = run.ErrorMessage;
        if (runStatus == WorkflowRunStatus.Failed && string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = stepStatesOrdered
                .Where(s => s.Status == WorkflowStepStatus.Failed)
                .Select(s => $"Step '{s.StepId}' failed: {s.ErrorMessage}")
                .FirstOrDefault();
        }

        if (!changed && runStatus == run.Status && completedAt == run.CompletedAt)
        {
            return run;
        }

        return run with
        {
            Status = runStatus,
            StepStates = stepStatesOrdered,
            UpdatedAt = now,
            CompletedAt = completedAt,
            ErrorMessage = errorMessage,
            Warnings = warnings
        };
    }

    private async Task<(WorkflowStepState? NewState, bool Changed)> SubmitStepAsync(
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowStepDefinition stepDefinition,
        WorkflowStepState state,
        IReadOnlyDictionary<string, WorkflowStepState> allStates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var attemptNumber = state.AttemptCount + 1;
        using var activity = OrchestrationTelemetry.StartExecuteStepActivity(
            run.RunId,
            state.StepId,
            stepDefinition.Plan.PlanId,
            attemptNumber);

        using var bindingActivity = OrchestrationTelemetry.StartResolveBindingsActivity(
            run.RunId,
            state.StepId,
            stepDefinition.InputBindings.Count);
        var bindingResolution = WorkflowBindingResolver.Resolve(stepDefinition, allStates);
        bindingActivity?.Dispose();

        if (bindingResolution.Failures.Count > 0)
        {
            foreach (var failure in bindingResolution.Failures)
            {
                warnings.Add($"{state.StepId}: {failure}");
                OrchestrationLog.InputBindingFailed(_logger, run.RunId, state.StepId, "binding", failure);
            }

            var failed = state with
            {
                Status = WorkflowStepStatus.Failed,
                AttemptCount = attemptNumber,
                CompletedAt = now,
                ErrorMessage = string.Join("; ", bindingResolution.Failures)
            };
            OrchestrationTelemetry.StepsCompleted.Add(
                1,
                new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, failed.Status.ToString()));
            return (failed, true);
        }

        foreach (var pair in bindingResolution.ResolvedValues)
        {
            OrchestrationLog.InputBindingResolved(_logger, run.RunId, state.StepId, pair.Key, pair.Value);
        }

        // Prefer the plan the gate authorized at RUN CREATION: it carries the bindings produced
        // for THIS run's expanded step, which is the only place a ForEach step's concrete layer
        // and dataset ids are known. The stored definition is the fallback for runs created
        // before that plan was persisted, where publication's binding on a static layer id is
        // what travels (#3043). Either way the submit-time gate matches the binding against the
        // layer the dataset resolves to NOW and refuses on a mismatch, so a dataset re-pointed
        // between authorization and dispatch fails this step rather than being re-authorized
        // under the orchestrator identity below.
        var basePlan = state.AuthorizedPlan ?? stepDefinition.Plan;
        var planForAttempt = WorkflowBindingResolver.ApplyBindings(basePlan, bindingResolution.ResolvedValues);
        var idempotencyKey = $"{run.RunId}:{state.StepId}:{attemptNumber}";
        var principal = OrchestrationSystemPrincipal.Create(run.Audit.RequestedBy);
        var protocolMetadata = BuildOrchestrationMetadata(run, state.StepId, attemptNumber, stepDefinition.TimeoutSeconds);

        ExecutionJobRecord jobRecord;
        try
        {
            jobRecord = await _jobService.SubmitJobAsync(
                planForAttempt,
                idempotencyKey,
                principal,
                protocolMetadata,
                run.Audit.SubmitterSecurityContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrchestrationLog.WorkflowStepFailed(_logger, run.RunId, state.StepId, ex.Message);
            var (newStatus, scheduledAt) = ComputeFailureDisposition(stepDefinition, state, attemptNumber, now);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            if (newStatus == WorkflowStepStatus.Pending)
            {
                OrchestrationTelemetry.StepsRetried.Add(1);
                OrchestrationLog.WorkflowStepRetrying(_logger, run.RunId, state.StepId, attemptNumber + 1, scheduledAt ?? now);
            }
            else
            {
                OrchestrationTelemetry.StepsCompleted.Add(
                    1,
                    new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, newStatus.ToString()));
            }

            return (state with
            {
                Status = newStatus,
                AttemptCount = attemptNumber,
                NextAttemptAt = scheduledAt,
                CompletedAt = IsStepTerminal(newStatus) ? now : null,
                ErrorMessage = "Workflow step submission failed.",
                ResolvedInputs = bindingResolution.ResolvedValues,
                StartedAt = newStatus == WorkflowStepStatus.Pending ? null : state.StartedAt,
                JobId = newStatus == WorkflowStepStatus.Pending ? null : state.JobId
            }, true);
        }

        OrchestrationLog.WorkflowStepSubmitted(_logger, run.RunId, state.StepId, jobRecord.OperationId, attemptNumber);

        // When SubmitJobAsync returns an already-terminal record (idempotent replay after
        // crash), route through ObserveStepAsync so replayed successes fetch artifacts,
        // replayed failures apply retry policy, and replayed cancellations stamp CompletedAt.
        var submittedStepStatus = MapJobStatusToStepStatus(jobRecord.Status);
        if (IsStepTerminal(submittedStepStatus))
        {
            var replayState = state with
            {
                JobId = jobRecord.OperationId,
                AttemptCount = attemptNumber,
                StartedAt = state.StartedAt ?? now,
                NextAttemptAt = null,
                ResolvedInputs = bindingResolution.ResolvedValues,
                ErrorMessage = null
            };
            var projection = await ObserveStepAsync(run, definition, stepDefinition, replayState, warnings, cancellationToken).ConfigureAwait(false);
            return (projection ?? replayState, true);
        }

        return (state with
        {
            Status = submittedStepStatus,
            JobId = jobRecord.OperationId,
            AttemptCount = attemptNumber,
            StartedAt = state.StartedAt ?? now,
            NextAttemptAt = null,
            ResolvedInputs = bindingResolution.ResolvedValues,
            ErrorMessage = null
        }, true);
    }

    private async Task<WorkflowStepState?> ObserveStepAsync(
        WorkflowRun run,
        WorkflowDefinition definition,
        WorkflowStepDefinition stepDefinition,
        WorkflowStepState state,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.JobId))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var principal = OrchestrationSystemPrincipal.Create(run.Audit.RequestedBy);

        ExecutionJobRecord job;
        try
        {
            job = await _jobService.GetJobAsync(state.JobId, principal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrchestrationLog.WorkflowStepObservationTransientFailure(_logger, run.RunId, state.StepId, state.JobId!, ex);
            AddOrReplaceObservationWarning(warnings, state.StepId, state.JobId!, "Job state could not be observed.");
            return null;
        }

        var newStepStatus = MapJobStatusToStepStatus(job.Status);
        if (newStepStatus == state.Status && job.Status != ExecutionJobStatus.Succeeded && job.Status != ExecutionJobStatus.Failed)
        {
            return null;
        }

        switch (job.Status)
        {
            case ExecutionJobStatus.Succeeded:
                {
                    IReadOnlyList<ArtifactRef>? artifacts = null;
                    Exception? artifactsFailure = null;
                    try
                    {
                        var results = await _jobService.GetJobResultsAsync(state.JobId!, principal, cancellationToken).ConfigureAwait(false);
                        artifacts = results.Artifacts;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        artifactsFailure = ex;
                        OrchestrationLog.InputBindingFailed(_logger, run.RunId, state.StepId, "<results>", ex.Message);
                    }

                    // If artifact retrieval failed and any downstream step binds artifacts from
                    // this step, the workflow cannot succeed end-to-end: the binding resolver
                    // will fail the dependent with a confusing "could not resolve selector"
                    // error and the run status derivation marks the parent Succeeded-with-null
                    // artifacts. Surface the failure at this step instead so operators see the
                    // real cause (e.g. terminal result synthesis or artifact publication failure), and the run fails
                    // fast rather than silently skipping artifact chaining.
                    if (artifactsFailure is not null &&
                        (artifacts is null || artifacts.Count == 0) &&
                        HasDownstreamBindingFromStep(definition, state.StepId))
                    {
                        OrchestrationLog.WorkflowStepArtifactsUnavailableForBoundDependents(
                            _logger,
                            run.RunId,
                            state.StepId,
                            artifactsFailure);
                        OrchestrationTelemetry.StepDuration.Record(
                            (now - (state.StartedAt ?? job.CreatedAt)).TotalMilliseconds,
                            new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, WorkflowStepStatus.Failed.ToString()));
                        OrchestrationTelemetry.StepsCompleted.Add(
                            1,
                            new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, WorkflowStepStatus.Failed.ToString()));
                        return state with
                        {
                            Status = WorkflowStepStatus.Failed,
                            CompletedAt = now,
                            OutputArtifacts = null,
                            ErrorMessage =
                                $"Step '{state.StepId}' job succeeded but artifact retrieval failed; " +
                                "downstream bindings cannot be resolved."
                        };
                    }

                    var duration = (now - (state.StartedAt ?? job.CreatedAt)).TotalMilliseconds;
                    OrchestrationTelemetry.StepDuration.Record(
                        duration,
                        new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, WorkflowStepStatus.Succeeded.ToString()));
                    OrchestrationTelemetry.StepsCompleted.Add(
                        1,
                        new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, WorkflowStepStatus.Succeeded.ToString()));
                    OrchestrationLog.WorkflowStepCompleted(_logger, run.RunId, state.StepId, WorkflowStepStatus.Succeeded.ToString());
                    return state with
                    {
                        Status = WorkflowStepStatus.Succeeded,
                        CompletedAt = now,
                        OutputArtifacts = artifacts,
                        ErrorMessage = null
                    };
                }

            case ExecutionJobStatus.Failed:
                {
                    var reason = job.ErrorMessage ?? "job failed";
                    OrchestrationLog.WorkflowStepFailed(_logger, run.RunId, state.StepId, reason);
                    var (newStatus, scheduledAt) = ComputeFailureDisposition(stepDefinition, state, state.AttemptCount, now);

                    if (newStatus == WorkflowStepStatus.Pending)
                    {
                        OrchestrationTelemetry.StepsRetried.Add(1);
                        OrchestrationLog.WorkflowStepRetrying(_logger, run.RunId, state.StepId, state.AttemptCount + 1, scheduledAt ?? now);
                        return state with
                        {
                            Status = WorkflowStepStatus.Pending,
                            NextAttemptAt = scheduledAt,
                            ErrorMessage = reason,
                            CompletedAt = null,
                            StartedAt = null,
                            JobId = null
                        };
                    }

                    OrchestrationTelemetry.StepDuration.Record(
                        (now - (state.StartedAt ?? job.CreatedAt)).TotalMilliseconds,
                        new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, newStatus.ToString()));
                    OrchestrationTelemetry.StepsCompleted.Add(
                        1,
                        new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, newStatus.ToString()));
                    OrchestrationLog.WorkflowStepCompleted(_logger, run.RunId, state.StepId, newStatus.ToString());
                    return state with
                    {
                        Status = newStatus,
                        CompletedAt = now,
                        ErrorMessage = reason
                    };
                }

            case ExecutionJobStatus.Cancelled:
                {
                    OrchestrationLog.WorkflowStepCompleted(_logger, run.RunId, state.StepId, WorkflowStepStatus.Cancelled.ToString());
                    OrchestrationTelemetry.StepsCompleted.Add(
                        1,
                        new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, WorkflowStepStatus.Cancelled.ToString()));
                    return state with
                    {
                        Status = WorkflowStepStatus.Cancelled,
                        CompletedAt = now,
                        ErrorMessage = job.ErrorMessage
                    };
                }

            default:
                return state with { Status = newStepStatus };
        }
    }

    private static (WorkflowStepStatus Status, DateTimeOffset? NextAttemptAt) ComputeFailureDisposition(
        WorkflowStepDefinition stepDefinition,
        WorkflowStepState state,
        int completedAttempts,
        DateTimeOffset now)
    {
        var retryPolicy = stepDefinition.RetryPolicy;
        if (retryPolicy is not null && completedAttempts < retryPolicy.MaxAttempts)
        {
            var delay = retryPolicy.ComputeDelay(completedAttempts);
            return (WorkflowStepStatus.Pending, now + delay);
        }

        _ = state;
        return stepDefinition.FailurePolicy switch
        {
            WorkflowStepFailurePolicy.Skip => (WorkflowStepStatus.Skipped, (DateTimeOffset?)null),
            _ => (WorkflowStepStatus.Failed, (DateTimeOffset?)null)
        };
    }

    private static bool AreDependenciesSatisfied(
        WorkflowStepDefinition stepDefinition,
        IReadOnlyDictionary<string, WorkflowStepDefinition> definitionById,
        Dictionary<string, WorkflowStepState> states,
        out CascadeReason? cascade)
    {
        cascade = null;

        foreach (var dependency in stepDefinition.DependsOn)
        {
            if (!states.TryGetValue(dependency, out var upstream))
            {
                cascade = new CascadeReason(WorkflowStepStatus.Failed, $"Unknown dependency '{dependency}'.");
                return false;
            }

            switch (upstream.Status)
            {
                case WorkflowStepStatus.Succeeded:
                    continue;
                case WorkflowStepStatus.Skipped:
                    // Per the design contract, a Skip-policy upstream only cascades to dependents
                    // that actually consume its artifacts. Structural DependsOn alone is not enough
                    // to trigger a cascade skip; the downstream step proceeds as if satisfied.
                    if (!HasBindingFrom(stepDefinition, dependency))
                    {
                        continue;
                    }

                    cascade = new CascadeReason(WorkflowStepStatus.Skipped, $"Upstream step '{dependency}' was skipped.");
                    return false;
                case WorkflowStepStatus.Failed:
                    // A Fail-policy upstream cancels non-terminal dependents so the run records them
                    // as cancelled (not skipped), while the run itself derives Failed from the upstream.
                    cascade = new CascadeReason(
                        WorkflowStepStatus.Cancelled,
                        $"Upstream step '{dependency}' failed under {ResolveUpstreamPolicy(definitionById, dependency)} policy.");
                    return false;
                case WorkflowStepStatus.Cancelled:
                    cascade = new CascadeReason(WorkflowStepStatus.Cancelled, $"Upstream step '{dependency}' was cancelled.");
                    return false;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool HasBindingFrom(WorkflowStepDefinition stepDefinition, string upstreamStepId)
        => stepDefinition.InputBindings.Any(binding => string.Equals(binding.SourceStepId, upstreamStepId, StringComparison.Ordinal));

    private static bool HasDownstreamBindingFromStep(WorkflowDefinition definition, string upstreamStepId)
        => definition.Steps.Any(step => HasBindingFrom(step, upstreamStepId));

    private static string ResolveUpstreamPolicy(
        IReadOnlyDictionary<string, WorkflowStepDefinition> definitionById,
        string upstreamStepId)
        => definitionById.TryGetValue(upstreamStepId, out var def)
            ? def.FailurePolicy.ToString()
            : WorkflowStepFailurePolicy.Fail.ToString();

    private sealed record CascadeReason(WorkflowStepStatus Status, string Reason);

    private static WorkflowRunStatus DeriveRunStatus(IReadOnlyList<WorkflowStepState> states, WorkflowRunStatus current)
    {
        if (current == WorkflowRunStatus.Cancelled)
        {
            return WorkflowRunStatus.Cancelled;
        }

        var allTerminal = states.All(s => IsStepTerminal(s.Status));
        if (!allTerminal)
        {
            return states.Any(s => s.Status is WorkflowStepStatus.Queued or WorkflowStepStatus.Running)
                ? WorkflowRunStatus.Running
                : current == WorkflowRunStatus.Pending
                    ? WorkflowRunStatus.Running
                    : current;
        }

        if (states.Any(s => s.Status == WorkflowStepStatus.Failed))
        {
            return WorkflowRunStatus.Failed;
        }

        if (states.Any(s => s.Status == WorkflowStepStatus.Cancelled))
        {
            return WorkflowRunStatus.Cancelled;
        }

        return WorkflowRunStatus.Succeeded;
    }

    private async Task PersistRunAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        await _runStore.SetAsync(run, cancellationToken: cancellationToken).ConfigureAwait(false);

        var totalSteps = run.StepStates.Count;
        var completed = run.StepStates.Count(s => s.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped);

        try
        {
            var cancelCleanupInProgress = run.Status == WorkflowRunStatus.Cancelled &&
                !run.StepStates.All(s => IsStepTerminal(s.Status));
            var progress = new WorkflowProgress
            {
                OperationId = run.RunId,
                WorkflowId = run.WorkflowId,
                RunStatus = cancelCleanupInProgress ? WorkflowRunStatus.Running : run.Status,
                StepsCompleted = completed,
                TotalSteps = totalSteps,
                StartedAt = run.CreatedAt,
                CompletedAt = run.CompletedAt,
                ErrorMessage = run.ErrorMessage,
                Warnings = run.Warnings,
                CurrentPhase = cancelCleanupInProgress ? "Cancelling" : run.Status.ToString()
            };
            await _progressStore.SetProgressAsync(run.RunId, progress, ProgressRetention, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrchestrationLog.ProgressProjectionFailed(_logger, run.RunId, ex);
        }

        if (IsRunTerminal(run.Status) && run.StepStates.All(s => IsStepTerminal(s.Status)))
        {
            var duration = ((run.CompletedAt ?? _clock.GetUtcNow()) - run.CreatedAt).TotalMilliseconds;
            OrchestrationTelemetry.RunDuration.Record(
                duration,
                new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.RunStatus, run.Status.ToString()));
            OrchestrationTelemetry.RunsCompleted.Add(
                1,
                new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.RunStatus, run.Status.ToString()));
            OrchestrationLog.WorkflowRunCompleted(_logger, run.RunId, run.Status.ToString(), completed, totalSteps);
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(string runId, CancellationTokenSource reconciliationCancellation)
    {
        while (!reconciliationCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewInterval, reconciliationCancellation.Token).ConfigureAwait(false);
                var renewed = await _runStore.RenewLeaseAsync(
                    runId,
                    _ownerId,
                    LeaseDuration,
                    reconciliationCancellation.Token).ConfigureAwait(false);
                if (!renewed)
                {
                    reconciliationCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static Dictionary<string, string> BuildOrchestrationMetadata(
        WorkflowRun run,
        string stepId,
        int attemptNumber,
        int? timeoutSeconds)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orchestration.runId"] = run.RunId,
            ["orchestration.workflowId"] = run.WorkflowId,
            ["orchestration.stepId"] = stepId,
            ["orchestration.attempt"] = attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        // Surface the step-level timeout to the underlying job substrate so executor
        // adapters (and downstream cost/quota controls) can honour it without coupling
        // their contract to WorkflowStepDefinition. A null or non-positive value is
        // treated as "no workflow-level timeout" and is omitted from the metadata.
        if (timeoutSeconds is { } seconds && seconds > 0)
        {
            metadata["orchestration.timeoutSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static WorkflowStepStatus MapJobStatusToStepStatus(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => WorkflowStepStatus.Queued,
        ExecutionJobStatus.Provisioning => WorkflowStepStatus.Queued,
        ExecutionJobStatus.Running => WorkflowStepStatus.Running,
        ExecutionJobStatus.Succeeded => WorkflowStepStatus.Succeeded,
        ExecutionJobStatus.Failed => WorkflowStepStatus.Failed,
        ExecutionJobStatus.Cancelled => WorkflowStepStatus.Cancelled,
        _ => WorkflowStepStatus.Queued
    };

    private static bool IsRunTerminal(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;

    private static bool IsStepTerminal(WorkflowStepStatus status)
        => status is WorkflowStepStatus.Succeeded
            or WorkflowStepStatus.Failed
            or WorkflowStepStatus.Cancelled
            or WorkflowStepStatus.Skipped;

    private static void AddOrReplaceCancelWarning(List<string> warnings, string stepId, string jobId, string message)
    {
        var prefix = $"{stepId}: failed to cancel underlying job '{jobId}':";
        AddOrReplaceWarning(warnings, prefix, message);
    }

    private static void AddOrReplaceObservationWarning(List<string> warnings, string stepId, string jobId, string message)
    {
        var prefix = $"{stepId}: transient observation failure for job '{jobId}':";
        AddOrReplaceWarning(warnings, prefix, message);
    }

    private static void AddOrReplaceWarning(List<string> warnings, string prefix, string message)
    {
        for (var i = 0; i < warnings.Count; i++)
        {
            if (warnings[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                warnings[i] = $"{prefix} {message}";
                return;
            }
        }

        warnings.Add($"{prefix} {message}");
    }
}
