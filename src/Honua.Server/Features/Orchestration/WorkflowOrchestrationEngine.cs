// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;

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
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public WorkflowOrchestrationEngine(
        IWorkflowRunStore runStore,
        IWorkflowDefinitionStore definitionStore,
        IWorkflowJobExecutor jobService,
        IUniversalProgressStore progressStore,
        TimeProvider clock,
        ILogger<WorkflowOrchestrationEngine> logger)
    {
        _runStore = runStore;
        _definitionStore = definitionStore;
        _jobService = jobService;
        _progressStore = progressStore;
        _clock = clock;
        _logger = logger;
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

        var now = _clock.GetUtcNow();
        var runId = $"wf-{Guid.NewGuid():N}";
        var stepStates = definition.Steps
            .Select(step => new WorkflowStepState
            {
                StepId = step.StepId,
                PlanId = step.Plan.PlanId,
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0
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
                RequestedBy = principal.Identity?.Name
            }
        };

        var created = await _runStore.TryCreateAsync(run, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException($"Failed to persist workflow run '{runId}'.");
        }

        var progress = WorkflowProgress.CreateForPendingRun(runId, definition.WorkflowId, stepStates.Length, now);
        await _progressStore.SetProgressAsync(runId, progress, ProgressRetention, cancellationToken).ConfigureAwait(false);

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

            var definition = await _definitionStore.GetAsync(run.WorkflowId, reconciliationCancellation.Token).ConfigureAwait(false);
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
                        catch (Exception ex)
                        {
                            OrchestrationLog.WorkflowStepCancelJobFailed(_logger, run.RunId, s.StepId, s.JobId!, ex);
                            warnings.Add($"{s.StepId}: failed to cancel underlying job '{s.JobId}': {ex.Message}");
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
            var finalisedSteps = new WorkflowStepState[run.StepStates.Count];
            for (var i = 0; i < run.StepStates.Count; i++)
            {
                var s = run.StepStates[i];
                if (IsStepTerminal(s.Status))
                {
                    finalisedSteps[i] = s;
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
                    catch (Exception) { }
                }

                finalisedSteps[i] = s with
                {
                    Status = WorkflowStepStatus.Cancelled,
                    CompletedAt = now,
                    ErrorMessage = s.ErrorMessage ?? "Definition step-set changed since run creation."
                };
            }

            var mismatchStatus = IsRunTerminal(run.Status) ? run.Status : WorkflowRunStatus.Failed;
            return run with
            {
                Status = mismatchStatus,
                StepStates = finalisedSteps,
                UpdatedAt = now,
                CompletedAt = run.CompletedAt ?? now,
                ErrorMessage = $"Workflow definition '{run.WorkflowId}' step-set changed since run was created. {detail}"
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
                    catch (Exception ex)
                    {
                        OrchestrationLog.WorkflowStepCancelJobFailed(_logger, run.RunId, state.StepId, state.JobId!, ex);
                        warnings.Add($"{state.StepId}: failed to cancel underlying job '{state.JobId}': {ex.Message}");
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
                        var observation = await ObserveStepAsync(run, definition, definitionStep, state, cancellationToken).ConfigureAwait(false);
                        if (observation is not null)
                        {
                            states[state.StepId] = observation;
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

        var planForAttempt = WorkflowBindingResolver.ApplyBindings(stepDefinition.Plan, bindingResolution.ResolvedValues);
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OrchestrationLog.WorkflowStepFailed(_logger, run.RunId, state.StepId, ex.Message);
            var (newStatus, scheduledAt) = ComputeFailureDisposition(stepDefinition, state, attemptNumber, now);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            OrchestrationTelemetry.StepsCompleted.Add(
                1,
                new KeyValuePair<string, object?>(OrchestrationTelemetry.Tags.StepStatus, newStatus.ToString()));

            if (newStatus == WorkflowStepStatus.Pending)
            {
                OrchestrationTelemetry.StepsRetried.Add(1);
                OrchestrationLog.WorkflowStepRetrying(_logger, run.RunId, state.StepId, attemptNumber + 1, scheduledAt ?? now);
            }

            return (state with
            {
                Status = newStatus,
                AttemptCount = attemptNumber,
                NextAttemptAt = scheduledAt,
                CompletedAt = IsStepTerminal(newStatus) ? now : null,
                ErrorMessage = ex.Message,
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
            var projection = await ObserveStepAsync(run, definition, stepDefinition, replayState, cancellationToken).ConfigureAwait(false);
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
            // Observation transport errors (store outage, transient network failure) must not
            // terminalise a workflow step: the underlying job may still be healthy. Leave the
            // state untouched so the reconcile loop retries, and surface the failure via
            // telemetry and a run-level warning for operator visibility.
            OrchestrationLog.WorkflowStepObservationTransientFailure(_logger, run.RunId, state.StepId, state.JobId!, ex);
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
                    // real cause (e.g. "result package not yet available"), and the run fails
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
                                $"downstream bindings cannot be resolved: {artifactsFailure.Message}"
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

    private (WorkflowStepStatus Status, DateTimeOffset? NextAttemptAt) ComputeFailureDisposition(
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
    {
        foreach (var binding in stepDefinition.InputBindings)
        {
            if (string.Equals(binding.SourceStepId, upstreamStepId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDownstreamBindingFromStep(WorkflowDefinition definition, string upstreamStepId)
    {
        foreach (var step in definition.Steps)
        {
            if (HasBindingFrom(step, upstreamStepId))
            {
                return true;
            }
        }

        return false;
    }

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

        var startedAt = run.CreatedAt;
        var totalSteps = run.StepStates.Count;
        var completed = run.StepStates.Count(s => s.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped);
        var cancelCleanupInProgress = run.Status == WorkflowRunStatus.Cancelled &&
            !run.StepStates.All(s => IsStepTerminal(s.Status));
        var progress = new WorkflowProgress
        {
            OperationId = run.RunId,
            WorkflowId = run.WorkflowId,
            RunStatus = cancelCleanupInProgress ? WorkflowRunStatus.Running : run.Status,
            StepsCompleted = completed,
            TotalSteps = totalSteps,
            StartedAt = startedAt,
            CompletedAt = run.CompletedAt,
            ErrorMessage = run.ErrorMessage,
            Warnings = run.Warnings,
            CurrentPhase = cancelCleanupInProgress ? "Cancelling" : run.Status.ToString()
        };
        await _progressStore.SetProgressAsync(run.RunId, progress, ProgressRetention, cancellationToken).ConfigureAwait(false);

        // Only emit terminal run telemetry when the run has actually finished in full.
        // CancelRunAsync flips the run status to Cancelled before cascade step cleanup has
        // run, and a follow-up reconcile persists the run again once every step is terminal.
        // Gating on both the run and every step being terminal keeps telemetry to one
        // emission per run — otherwise cancelled runs would double-count in run_duration_ms,
        // runs_completed_total, and the WorkflowRunCompleted log line.
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
}
