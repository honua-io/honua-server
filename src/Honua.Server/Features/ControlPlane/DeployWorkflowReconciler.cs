// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.ControlPlane;

/// <summary>
/// Reconciles durable deploy workflow operations against provider backends.
/// </summary>
internal sealed partial class DeployWorkflowReconciler(
    IWorkflowOperationStore workflowStore,
    IDeployTargetRegistry targetRegistry,
    IEnumerable<IDeployBackend> backends,
    IDeployTelemetrySignalEvaluator telemetrySignalEvaluator,
    ILogger<DeployWorkflowReconciler> logger) : IWorkflowOperationReconciler
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(10);
    private readonly Dictionary<(string Backend, DeployTargetKind TargetKind), IDeployBackend> _backends = backends.ToDictionary(
        backend => (backend.BackendName, backend.TargetKind),
        backend => backend,
        EqualityComparer<(string Backend, DeployTargetKind TargetKind)>.Default);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task ReconcileWorkflowOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var leaseAcquired = await workflowStore.TryAcquireLeaseAsync(operationId, _ownerId, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (!leaseAcquired)
        {
            return;
        }

        using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewLeaseUntilCancelledAsync(operationId, reconciliationCancellation);

        try
        {
            var operation = await workflowStore.GetAsync(operationId, reconciliationCancellation.Token).ConfigureAwait(false);
            if (operation == null ||
                operation.Kind != WorkflowOperationKind.Deploy ||
                operation.Deploy == null ||
                IsTerminal(operation.Status) ||
                operation.Status is not (WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.RollbackRequested))
            {
                return;
            }

            var target = await targetRegistry.GetAsync(operation.Deploy.TargetId, reconciliationCancellation.Token).ConfigureAwait(false);
            WorkflowOperationRecord updated;

            if (target == null || !_backends.TryGetValue((target.Backend, target.TargetKind), out var backend))
            {
                updated = operation with
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Deploy reconciliation requires manual intervention because no backend is registered for this target.",
                    ErrorMessage = $"No deploy backend is registered for target '{operation.Deploy.TargetId}' ({operation.Deploy.Backend})."
                };
            }
            else
            {
                var observation = await backend.ObserveAsync(operation, reconciliationCancellation.Token).ConfigureAwait(false);
                updated = operation with
                {
                    Status = observation.Status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = IsTerminal(observation.Status) ? DateTimeOffset.UtcNow : null,
                    ProviderOperationId = observation.ProviderOperationId ?? operation.ProviderOperationId,
                    CurrentPhase = observation.Message ?? operation.CurrentPhase,
                    ObservedState = observation.ObservedRevision ?? operation.ObservedState,
                    ErrorMessage = observation.Status == WorkflowOperationStatus.Failed
                        ? observation.Message ?? operation.ErrorMessage
                        : null,
                    Deploy = operation.Deploy with
                    {
                        CurrentRevision = string.IsNullOrWhiteSpace(operation.Deploy.CurrentRevision)
                            ? observation.ObservedRevision ?? operation.Deploy.CurrentRevision
                            : operation.Deploy.CurrentRevision
                    }
                };

                updated = await ApplyRollbackSignalsAsync(
                        operation,
                        updated,
                        backend,
                        observation.PromotionRecommended,
                        observation.RollbackRecommended,
                        observation.Message,
                        telemetrySignalEvaluator,
                        reconciliationCancellation.Token)
                    .ConfigureAwait(false);
            }

            if (!Equals(updated, operation))
            {
                await workflowStore.SetAsync(updated, cancellationToken: reconciliationCancellation.Token).ConfigureAwait(false);
                Log.WorkflowOperationReconciled(logger, operationId, updated.Status.ToString());
            }
        }
        catch (OperationCanceledException) when (reconciliationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.WorkflowOperationLeaseLost(logger, operationId);
            return;
        }
        catch (Exception ex)
        {
            var operation = await workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is
                {
                    Kind: WorkflowOperationKind.Deploy,
                    Deploy: not null
                } &&
                !IsTerminal(operation.Status))
            {
                var failedAt = DateTimeOffset.UtcNow;
                var failedOperation = operation with
                {
                    Status = WorkflowOperationStatus.ManualInterventionRequired,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    CurrentPhase = "Deploy reconciliation failed and requires manual intervention.",
                    ErrorMessage = $"Deploy reconciliation failed due to {ex.GetType().Name}."
                };

                await workflowStore.SetAsync(failedOperation, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

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

            await workflowStore.ReleaseLeaseAsync(operationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(string operationId, CancellationTokenSource reconciliationCancellation)
    {
        while (!reconciliationCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LeaseRenewInterval, reconciliationCancellation.Token).ConfigureAwait(false);
                var renewed = await workflowStore.RenewLeaseAsync(
                    operationId,
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

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private static async Task<WorkflowOperationRecord> ApplyRollbackSignalsAsync(
        WorkflowOperationRecord previous,
        WorkflowOperationRecord current,
        IDeployBackend backend,
        bool promotionRecommended,
        bool backendRollbackRecommended,
        string? backendMessage,
        IDeployTelemetrySignalEvaluator telemetrySignalEvaluator,
        CancellationToken cancellationToken)
    {
        var rollbackReason = backendRollbackRecommended
            ? backendMessage ?? "Deploy backend recommended rollback."
            : null;

        if (current.Status is WorkflowOperationStatus.Submitted or WorkflowOperationStatus.Reconciling or WorkflowOperationStatus.Succeeded)
        {
            // When a progressive canary ramp is configured the current step must finish its
            // bake window before the telemetry gate is evaluated. Holding here keeps each step
            // independent of the operation-wide warmup window and preserves single-step behavior
            // (the default) when no ramp is configured. A backend-recommended rollback bypasses
            // the hold so provider-detected failures still settle promptly.
            if (string.IsNullOrWhiteSpace(rollbackReason))
            {
                var rampBakeHold = GetCanaryRampBakeHold(current);
                if (rampBakeHold != null)
                {
                    return rampBakeHold;
                }
            }

            var telemetryDecision = await telemetrySignalEvaluator.EvaluateAsync(current, cancellationToken).ConfigureAwait(false);
            if (telemetryDecision != null)
            {
                // Persist any parameter updates the evaluator carried back (e.g. the anti-flap
                // consecutive-breach streak) so they survive to the next reconcile cycle.
                if (telemetryDecision.UpdatedDeployParameters != null && current.Deploy != null)
                {
                    current = current with
                    {
                        Deploy = current.Deploy with { Parameters = telemetryDecision.UpdatedDeployParameters }
                    };
                }

                if (telemetryDecision.RollbackRecommended)
                {
                    rollbackReason = telemetryDecision.Message;
                }
                else if (telemetryDecision.WaitForMoreTelemetry)
                {
                    current = current with
                    {
                        Status = WorkflowOperationStatus.Reconciling,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CompletedAt = null,
                        CurrentPhase = telemetryDecision.Message,
                        ErrorMessage = null
                    };
                }
                else if (!string.IsNullOrWhiteSpace(telemetryDecision.Message))
                {
                    current = current with
                    {
                        CurrentPhase = telemetryDecision.Message,
                        ErrorMessage = current.Status == WorkflowOperationStatus.Failed ? telemetryDecision.Message : current.ErrorMessage
                    };
                }

                if (!telemetryDecision.RollbackRecommended &&
                    !telemetryDecision.WaitForMoreTelemetry &&
                    promotionRecommended &&
                    current.Status == WorkflowOperationStatus.Reconciling)
                {
                    current = await AdvanceOrPromoteAsync(current, backend, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(rollbackReason) ||
            current.Status is WorkflowOperationStatus.RollbackRequested or WorkflowOperationStatus.RolledBack)
        {
            return current;
        }

        var deploySpec = current.Deploy;
        if (deploySpec == null)
        {
            return current;
        }

        var rollbackObservation = await backend.RollbackAsync(current, cancellationToken).ConfigureAwait(false);
        var updatedAt = DateTimeOffset.UtcNow;

        // Failure-of-failure handling (#2161): a rollback that itself fails must not silently park the
        // deploy, but it must also not page an operator on the FIRST transient provider blip. The
        // backends signal two distinct failure shapes from RollbackAsync, and they require different
        // responses:
        //
        //   * TERMINAL/HARD failure — the backend returns Failed (ECS/Argo/GitOps SanitizedFailure) or
        //     ManualInterventionRequired (e.g. Lambda when no prior version was captured). These are
        //     deterministic; retrying cannot help. Escalate immediately so an operator is paged.
        //
        //   * TRANSIENT failure — the Lambda backend (and any backend that mirrors it) echoes the
        //     operation's pre-rollback status (e.g. Reconciling/Submitted/Succeeded) with a "will
        //     retry" message on a throttle or other transient AWS error. The rollback did not take,
        //     but the next reconcile cycle can re-drive it. A single throttle must NOT abort self-
        //     healing. Retry on a bounded budget (RollbackRetryBudget attempts), then escalate so a
        //     persistently-failing rollback still pages loudly rather than looping forever (the
        //     original #2161 bug).
        //
        // Progress statuses (RollbackRequested/RolledBack) fall through to the success path below.
        var rollbackTerminallyFailed = rollbackObservation.Status
            is WorkflowOperationStatus.Failed or WorkflowOperationStatus.ManualInterventionRequired;
        var rollbackInProgress = rollbackObservation.Status
            is WorkflowOperationStatus.RollbackRequested or WorkflowOperationStatus.RolledBack;
        var rollbackTransientlyFailed = !rollbackTerminallyFailed && !rollbackInProgress;

        if (rollbackTransientlyFailed)
        {
            // Count this cycle's transient failure. The attempt budget is carried in the deploy spec
            // parameters (mirroring the telemetry breach-streak pattern) so it survives to the next
            // reconcile without a separate store. While there is retry budget remaining we keep the
            // operation in a re-drivable non-terminal status (Reconciling) — the RollbackRequested
            // guard above only short-circuits RollbackRequested/RolledBack, so Reconciling re-enters
            // RollbackAsync next cycle and the transient blip self-heals.
            var attempts = ReadRollbackAttempts(deploySpec) + 1;
            var budget = ResolveRollbackRetryBudget(deploySpec);
            var transientDetail = rollbackObservation.Message ?? rollbackReason;

            if (attempts < budget)
            {
                return current with
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    UpdatedAt = updatedAt,
                    CompletedAt = null,
                    ProviderOperationId = rollbackObservation.ProviderOperationId ?? current.ProviderOperationId,
                    CurrentPhase = $"Automatic rollback did not take on attempt {attempts} of {budget} and will be retried. {transientDetail}",
                    ObservedState = rollbackObservation.ObservedRevision ?? current.ObservedState,
                    ErrorMessage = null,
                    Deploy = WithRollbackAttempts(deploySpec, attempts)
                };
            }

            // Budget exhausted: a rollback that never settles after the bounded retries is treated as
            // genuinely stuck. Escalate to manual intervention so an operator is paged.
            return current with
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                UpdatedAt = updatedAt,
                CompletedAt = updatedAt,
                ProviderOperationId = rollbackObservation.ProviderOperationId ?? current.ProviderOperationId,
                CurrentPhase = $"Automatic rollback could not be completed after {attempts} attempts and requires manual intervention. {transientDetail}",
                ObservedState = rollbackObservation.ObservedRevision ?? current.ObservedState,
                ErrorMessage = $"Automatic rollback failed after {attempts} attempts: {transientDetail}",
                Deploy = WithRollbackAttempts(deploySpec, attempts) with
                {
                    CurrentRevision = string.IsNullOrWhiteSpace(deploySpec.CurrentRevision)
                        ? rollbackObservation.ObservedRevision ?? deploySpec.CurrentRevision
                        : deploySpec.CurrentRevision
                }
            };
        }

        if (rollbackTerminallyFailed)
        {
            var rollbackFailureDetail = rollbackObservation.Message ?? rollbackReason;
            return current with
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                UpdatedAt = updatedAt,
                CompletedAt = updatedAt,
                ProviderOperationId = rollbackObservation.ProviderOperationId ?? current.ProviderOperationId,
                CurrentPhase = $"Automatic rollback could not be completed and requires manual intervention. {rollbackFailureDetail}",
                ObservedState = rollbackObservation.ObservedRevision ?? current.ObservedState,
                ErrorMessage = $"Automatic rollback failed: {rollbackFailureDetail}",
                Deploy = deploySpec with
                {
                    CurrentRevision = string.IsNullOrWhiteSpace(deploySpec.CurrentRevision)
                        ? rollbackObservation.ObservedRevision ?? deploySpec.CurrentRevision
                        : deploySpec.CurrentRevision
                }
            };
        }

        return current with
        {
            Status = rollbackObservation.Status,
            UpdatedAt = updatedAt,
            CompletedAt = IsTerminal(rollbackObservation.Status) ? updatedAt : null,
            ProviderOperationId = rollbackObservation.ProviderOperationId ?? current.ProviderOperationId,
            CurrentPhase = rollbackReason,
            ObservedState = rollbackObservation.ObservedRevision ?? current.ObservedState,
            ErrorMessage = rollbackReason,
            // The rollback took (RollbackRequested/RolledBack): clear any transient-retry attempt
            // counter so the recorded spec reflects a clean settle rather than the in-flight budget.
            Deploy = WithoutRollbackAttempts(deploySpec) with
            {
                CurrentRevision = string.IsNullOrWhiteSpace(deploySpec.CurrentRevision)
                    ? rollbackObservation.ObservedRevision ?? deploySpec.CurrentRevision
                    : deploySpec.CurrentRevision
            }
        };
    }

    /// <summary>
    /// Reserved deploy-spec parameter key carrying the count of consecutive transient rollback
    /// failures between reconcile cycles. The reconciler persists the updated spec parameters, so the
    /// attempt budget survives without a separate store (mirroring the telemetry breach-streak key).
    /// </summary>
    internal const string RollbackTransientAttemptsParameterKey = "deployment.rollback.transient_attempts";

    /// <summary>
    /// Reserved deploy-spec parameter key bounding how many consecutive transient rollback failures
    /// are retried before the reconciler escalates to manual intervention. Defaults to
    /// <see cref="DefaultRollbackRetryBudget"/>; clamped to <see cref="MaximumRollbackRetryBudget"/>.
    /// </summary>
    internal const string RollbackRetryBudgetParameterKey = "deployment.rollback.retry_budget";

    private const int DefaultRollbackRetryBudget = 3;
    private const int MaximumRollbackRetryBudget = 10;

    private static int ReadRollbackAttempts(DeployOperationSpec spec)
        => spec.Parameters.TryGetValue(RollbackTransientAttemptsParameterKey, out var raw) &&
           int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts) &&
           attempts > 0
            ? attempts
            : 0;

    private static int ResolveRollbackRetryBudget(DeployOperationSpec spec)
    {
        if (spec.Parameters.TryGetValue(RollbackRetryBudgetParameterKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) &&
            budget > 0)
        {
            // Clamp so a misconfiguration can never re-create the unbounded-retry loop the original
            // #2161 escalation fixed — a persistently-failing rollback always escalates eventually.
            return Math.Min(budget, MaximumRollbackRetryBudget);
        }

        return DefaultRollbackRetryBudget;
    }

    private static DeployOperationSpec WithRollbackAttempts(DeployOperationSpec spec, int attempts)
        => spec with
        {
            Parameters = new Dictionary<string, string>(spec.Parameters, StringComparer.Ordinal)
            {
                [RollbackTransientAttemptsParameterKey] = attempts.ToString(CultureInfo.InvariantCulture)
            }
        };

    private static DeployOperationSpec WithoutRollbackAttempts(DeployOperationSpec spec)
    {
        if (!spec.Parameters.ContainsKey(RollbackTransientAttemptsParameterKey))
        {
            return spec;
        }

        var parameters = new Dictionary<string, string>(spec.Parameters, StringComparer.Ordinal);
        parameters.Remove(RollbackTransientAttemptsParameterKey);
        return spec with { Parameters = parameters };
    }

    /// <summary>
    /// Generic backend-neutral key the deploy backends read to resolve the desired canary
    /// traffic share. The progressive ramp drives stepped weights through this key so the
    /// existing <see cref="IDeployBackend"/> contract does not need a weight argument.
    /// </summary>
    private const string CanaryWeightParameterKey = "deployment.canary_weight_percentage";

    /// <summary>
    /// Holds the current ramp step until its bake window elapses. Returns null when no ramp is
    /// configured (single-step default), the ramp is invalid, or the step has finished baking.
    /// Lazily initializes the first step's bake clock and synchronizes the canary weight
    /// parameter so the backend applies the configured step weight.
    /// </summary>
    private static WorkflowOperationRecord? GetCanaryRampBakeHold(WorkflowOperationRecord current)
    {
        var deploy = current.Deploy;
        var ramp = deploy?.CanaryRamp;
        if (deploy == null || ramp == null || !ramp.IsValid)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var stepWeight = ramp.CurrentStepWeight ?? 100;

        if (ramp.CurrentStepStartedAt == null)
        {
            // First observation of this step: start its bake clock and pin the step weight onto
            // the spec parameters so the backend serves the configured share on the next apply.
            var initialized = current with
            {
                UpdatedAt = now,
                CurrentPhase = $"Canary ramp baking step {ramp.CurrentStepIndex + 1} of {ramp.StepWeights.Count} at {stepWeight}% for {DescribeDuration(ramp.StepBakeDuration)} before the telemetry gate.",
                Deploy = deploy with
                {
                    Parameters = WithCanaryWeight(deploy.Parameters, stepWeight),
                    CanaryRamp = ramp with { CurrentStepStartedAt = now }
                }
            };

            return initialized;
        }

        var elapsed = now - ramp.CurrentStepStartedAt.Value;
        if (elapsed >= ramp.StepBakeDuration)
        {
            return null;
        }

        var remaining = ramp.StepBakeDuration - elapsed;
        return current with
        {
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = now,
            CompletedAt = null,
            CurrentPhase = $"Canary ramp baking step {ramp.CurrentStepIndex + 1} of {ramp.StepWeights.Count} at {stepWeight}% ({Math.Ceiling(Math.Max(remaining.TotalSeconds, 0)).ToString("0", CultureInfo.InvariantCulture)}s remaining before the telemetry gate).",
            ErrorMessage = null
        };
    }

    /// <summary>
    /// Advances the progressive canary ramp to the next step (applying the next weight through
    /// <see cref="IDeployBackend.StartAsync"/>) or promotes the deploy to full traffic at the
    /// terminal step. Without a configured ramp this preserves the single-step default by calling
    /// <see cref="IDeployBackend.PromoteAsync"/> once.
    /// </summary>
    private static async Task<WorkflowOperationRecord> AdvanceOrPromoteAsync(
        WorkflowOperationRecord current,
        IDeployBackend backend,
        CancellationToken cancellationToken)
    {
        var deploy = current.Deploy;
        var ramp = deploy?.CanaryRamp;

        if (deploy != null && ramp != null && ramp.IsValid && !ramp.IsFinalStep)
        {
            var nextIndex = ramp.CurrentStepIndex + 1;
            var nextWeight = ramp.StepWeights[nextIndex];
            var rampedSpec = deploy with
            {
                Parameters = WithCanaryWeight(deploy.Parameters, nextWeight),
                CanaryRamp = ramp with
                {
                    CurrentStepIndex = nextIndex,
                    CurrentStepStartedAt = DateTimeOffset.UtcNow
                }
            };
            var rampedOperation = current with { Deploy = rampedSpec };

            // Re-apply the deploy at the next step weight. StartAsync resolves the canary share
            // from the synchronized parameter, so backends shift traffic without a new contract.
            var stepObservation = await backend.StartAsync(rampedOperation, cancellationToken).ConfigureAwait(false);
            var stepStatus = stepObservation.Status == WorkflowOperationStatus.Failed
                ? WorkflowOperationStatus.Failed
                : WorkflowOperationStatus.Reconciling;

            return rampedOperation with
            {
                Status = stepStatus,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = IsTerminal(stepStatus) ? DateTimeOffset.UtcNow : null,
                ProviderOperationId = stepObservation.ProviderOperationId ?? current.ProviderOperationId,
                CurrentPhase = stepObservation.Message
                    ?? $"Canary ramp advanced to step {nextIndex + 1} of {ramp.StepWeights.Count} at {nextWeight}%.",
                ObservedState = stepObservation.ObservedRevision ?? current.ObservedState,
                ErrorMessage = stepStatus == WorkflowOperationStatus.Failed
                    ? stepObservation.Message ?? current.ErrorMessage
                    : null
            };
        }

        var promotionObservation = await backend.PromoteAsync(current, cancellationToken).ConfigureAwait(false);
        return current with
        {
            Status = promotionObservation.Status,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = IsTerminal(promotionObservation.Status) ? DateTimeOffset.UtcNow : null,
            ProviderOperationId = promotionObservation.ProviderOperationId ?? current.ProviderOperationId,
            CurrentPhase = promotionObservation.Message ?? current.CurrentPhase,
            ObservedState = promotionObservation.ObservedRevision ?? current.ObservedState,
            ErrorMessage = promotionObservation.Status == WorkflowOperationStatus.Failed
                ? promotionObservation.Message ?? current.ErrorMessage
                : current.ErrorMessage
        };
    }

    private static Dictionary<string, string> WithCanaryWeight(
        IReadOnlyDictionary<string, string> parameters,
        int weight)
        => new(parameters, StringComparer.Ordinal)
        {
            [CanaryWeightParameterKey] = weight.ToString(CultureInfo.InvariantCulture)
        };

    private static string DescribeDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)}m"
            : $"{Math.Ceiling(duration.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)}s";

    internal static partial class Log
    {
        [LoggerMessage(9022, LogLevel.Debug, "Reconciled deploy workflow operation {OperationId} to status {Status}")]
        public static partial void WorkflowOperationReconciled(ILogger logger, string operationId, string status);

        [LoggerMessage(9023, LogLevel.Warning, "Deploy workflow reconciliation failed for operation {OperationId}")]
        public static partial void WorkflowOperationReconcileFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9024, LogLevel.Warning, "Deploy workflow reconciliation poll loop failed")]
        public static partial void WorkflowOperationPollLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(9026, LogLevel.Debug, "Deploy workflow reconciliation lease was lost for operation {OperationId}; another node may continue processing.")]
        public static partial void WorkflowOperationLeaseLost(ILogger logger, string operationId);
    }

    internal static partial class BackgroundLog
    {
        [LoggerMessage(9025, LogLevel.Information, "Started deploy workflow reconciliation background service")]
        public static partial void BackgroundServiceStarted(ILogger logger);
    }
}

/// <summary>
/// Background worker that continuously reconciles active deploy workflow operations.
/// </summary>
internal sealed class DeployWorkflowReconcilerBackgroundService(
    IWorkflowOperationStore workflowStore,
    IWorkflowOperationReconciler reconciler,
    ILogger<DeployWorkflowReconcilerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DeployWorkflowReconciler.BackgroundLog.BackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeOperations = await workflowStore.ListActiveAsync(WorkflowOperationKind.Deploy, stoppingToken).ConfigureAwait(false);
                foreach (var operation in activeOperations)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        DeployWorkflowReconciler.Log.WorkflowOperationReconcileFailed(logger, operation.OperationId, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DeployWorkflowReconciler.Log.WorkflowOperationPollLoopFailed(logger, ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
