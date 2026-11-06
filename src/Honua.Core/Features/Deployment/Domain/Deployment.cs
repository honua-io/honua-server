// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Deployment.Domain;

public sealed record Deployment
{
    public required string DeploymentId { get; init; }

    public long Version { get; init; }

    public required DeploymentSource Source { get; init; }

    public required DeploymentTarget Target { get; init; }

    public required DeploymentStatus Status { get; init; }

    public required DeploymentPublicationState PublicationState { get; init; }

    public RolloutPlan Rollout { get; init; } = RolloutPlan.Immediate();

    public RolloutState RolloutState { get; init; } = Domain.RolloutState.Pending;

    public int? CurrentRolloutStep { get; init; }

    public DeploymentSchedule? Schedule { get; init; }

    public RuntimeState Runtime { get; init; } = RuntimeState.Unknown();

    public OperationAuditInfo Audit { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ActivatedAt { get; init; }

    public DateTimeOffset? RetiredAt { get; init; }

    public string? FailureReason { get; init; }

    public string? SupersededByDeploymentId { get; init; }

    public string? ControlPlaneOperationId { get; init; }

    // Lifecycle entries are append-only; a runtime-only update does not append a transition.
    public IReadOnlyList<DeploymentTransition> Transitions { get; init; } = [];

    // A schedule-gated rollout must carry a publication schedule, or the deployment could activate immediately.
    public static Deployment CreateDraft(
        string deploymentId,
        DeploymentSource source,
        DeploymentTarget target,
        RolloutPlan? rollout = null,
        DeploymentSchedule? schedule = null,
        OperationAuditInfo? audit = null)
    {
        var effectiveRollout = rollout ?? RolloutPlan.Immediate();
        if (effectiveRollout.Strategy == RolloutStrategy.Scheduled && schedule is null)
        {
            throw new ArgumentException(
                "Scheduled rollout strategy requires a DeploymentSchedule to gate activation.",
                nameof(schedule));
        }

        var now = DateTimeOffset.UtcNow;
        return new Deployment
        {
            DeploymentId = deploymentId,
            Source = source,
            Target = target,
            Status = DeploymentStatus.Draft,
            PublicationState = DeploymentPublicationState.Draft,
            Rollout = effectiveRollout,
            Schedule = schedule,
            Audit = audit ?? new OperationAuditInfo(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public Deployment WithScheduled(
        DeploymentSchedule schedule,
        string? reason = null,
        OperationAuditInfo? audit = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        EnsureTransitionAllowed(
            DeploymentStatus.Scheduled,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled);
        return ApplyTransition(
            DeploymentStatus.Scheduled,
            schedule: schedule,
            publicationState: DeploymentPublicationState.Scheduled,
            reason: reason,
            audit: audit);
    }

    public Deployment WithProvisioning(string? reason = null, OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.Provisioning,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled);
        return ApplyTransition(DeploymentStatus.Provisioning, reason: reason, audit: audit);
    }

    // Re-entering rollout without a strictly later step is rejected so the audit trail never records a no-op transition.
    public Deployment WithRollingOut(
        int? step = null,
        string? reason = null,
        OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.RollingOut,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut);
        EnsureValidRolloutStep(step);
        return ApplyTransition(
            DeploymentStatus.RollingOut,
            rolloutState: Domain.RolloutState.InProgress,
            currentRolloutStep: step ?? CurrentRolloutStep ?? (Rollout.Strategy == RolloutStrategy.Canary ? 0 : null),
            reason: reason,
            audit: audit);
    }

    // Canary steps must strictly advance; a repeat or a regression is rejected.
    public Deployment WithRolloutStep(int step, OperationAuditInfo? audit = null)
    {
        if (Rollout.Strategy != RolloutStrategy.Canary)
        {
            throw new InvalidOperationException(
                "Rollout step can only advance on canary rollouts.");
        }

        if (Status != DeploymentStatus.RollingOut)
        {
            throw new InvalidOperationException(
                $"Rollout step can only advance while rolling out; current status is {Status}.");
        }

        if (step < 0 || step >= Rollout.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                $"Rollout step is outside the plan's range [0, {Rollout.Steps.Count - 1}].");
        }

        if (CurrentRolloutStep is int current && step <= current)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                $"Rollout step must strictly advance; current step is {current}.");
        }

        var now = DateTimeOffset.UtcNow;
        return this with
        {
            CurrentRolloutStep = step,
            RolloutState = Domain.RolloutState.InProgress,
            UpdatedAt = now,
            Transitions = AppendTransition(
                from: Status,
                to: Status,
                at: now,
                rolloutState: Domain.RolloutState.InProgress,
                reason: $"rollout_step_{step}",
                audit: audit)
        };
    }

    // Activation is confined to the publication window, a scheduled rollout requires its schedule, and a canary cannot activate before its final step.
    public Deployment WithActive(
        string? publicUrl = null,
        string? reason = null,
        OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.Active,
            DeploymentStatus.RollingOut,
            DeploymentStatus.Active);
        var now = DateTimeOffset.UtcNow;
        if (Rollout.Strategy == RolloutStrategy.Scheduled && Schedule is null)
        {
            throw new InvalidOperationException(
                "Cannot activate a scheduled rollout without an attached DeploymentSchedule.");
        }

        if (Status == DeploymentStatus.RollingOut && Rollout.Strategy == RolloutStrategy.Canary)
        {
            var finalStep = Rollout.Steps.Count - 1;
            if (CurrentRolloutStep is null || CurrentRolloutStep.Value != finalStep)
            {
                throw new InvalidOperationException(
                    "Cannot activate canary rollout before reaching the final rollout step.");
            }
        }

        if (Schedule is not null)
        {
            if (!Schedule.IsDue(now))
            {
                throw new InvalidOperationException(
                    "Cannot activate deployment before its scheduled publish time.");
            }

            if (Schedule.IsExpired(now))
            {
                throw new InvalidOperationException(
                    "Cannot activate deployment after its scheduled publish window has expired.");
            }
        }

        var runtime = Runtime with
        {
            PublicUrl = publicUrl ?? Runtime.PublicUrl,
            Health = Runtime.Health == RuntimeHealth.Unknown ? RuntimeHealth.Healthy : Runtime.Health,
            LastObservedAt = now
        };

        return ApplyTransition(
            DeploymentStatus.Active,
            rolloutState: Domain.RolloutState.Promoted,
            publicationState: DeploymentPublicationState.Published,
            runtime: runtime,
            activatedAt: ActivatedAt ?? now,
            now: now,
            reason: reason,
            audit: audit);
    }

    // Superseding a deployment mid-rollout marks the in-flight rollout so a terminal status is not stored beside an active rollout.
    public Deployment WithSuperseded(
        string successorDeploymentId,
        string? reason = null,
        OperationAuditInfo? audit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(successorDeploymentId);
        EnsureTransitionAllowed(
            DeploymentStatus.Superseded,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut,
            DeploymentStatus.Active);
        var now = DateTimeOffset.UtcNow;
        return ApplyTransition(
            DeploymentStatus.Superseded,
            rolloutState: ResolveTerminalRolloutState(),
            publicationState: DeploymentPublicationState.Unpublished,
            clearSchedule: true,
            supersededBy: successorDeploymentId,
            retiredAt: RetiredAt ?? now,
            now: now,
            reason: reason,
            audit: audit);
    }

    // Retiring a deployment mid-rollout marks the in-flight rollout so a terminal status is not stored beside an active rollout.
    public Deployment WithRetired(string? reason = null, OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.Retired,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut,
            DeploymentStatus.Active,
            DeploymentStatus.Superseded,
            DeploymentStatus.Failed);
        var now = DateTimeOffset.UtcNow;
        return ApplyTransition(
            DeploymentStatus.Retired,
            rolloutState: ResolveTerminalRolloutState(),
            publicationState: DeploymentPublicationState.Retired,
            clearSchedule: true,
            retiredAt: RetiredAt ?? now,
            now: now,
            reason: reason,
            audit: audit);
    }

    // An active deployment is not failed through this transition; it must be retired or superseded.
    public Deployment WithFailed(string failureReason, OperationAuditInfo? audit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureReason);
        EnsureTransitionAllowed(
            DeploymentStatus.Failed,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut);
        return ApplyTransition(
            DeploymentStatus.Failed,
            rolloutState: Domain.RolloutState.Failed,
            publicationState: DeploymentPublicationState.Unpublished,
            clearSchedule: true,
            failureReason: failureReason,
            reason: failureReason,
            audit: audit);
    }

    // Cancellation clears the pending schedule and unpublishes the record so a due time that will never be honored is not left behind.
    public Deployment WithCancelled(string? reason = null, OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.Cancelled,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut);
        return ApplyTransition(
            DeploymentStatus.Cancelled,
            rolloutState: Domain.RolloutState.Cancelled,
            publicationState: DeploymentPublicationState.Unpublished,
            clearSchedule: true,
            reason: reason,
            audit: audit);
    }

    // Rollback is valid only before promotion; an active deployment must be superseded or retired instead.
    public Deployment WithRollbackRequested(string reason, OperationAuditInfo? audit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        EnsureTransitionAllowed(
            DeploymentStatus.Failed,
            DeploymentStatus.Provisioning,
            DeploymentStatus.RollingOut);
        return ApplyTransition(
            DeploymentStatus.Failed,
            rolloutState: Domain.RolloutState.RolledBack,
            publicationState: DeploymentPublicationState.Unpublished,
            clearSchedule: true,
            failureReason: reason,
            reason: reason,
            audit: audit);
    }

    public Deployment WithRuntime(RuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return this with { Runtime = runtime, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public Deployment WithControlPlaneOperation(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        return this with { ControlPlaneOperationId = operationId, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private Deployment ApplyTransition(
        DeploymentStatus toStatus,
        RolloutState? rolloutState = null,
        int? currentRolloutStep = null,
        DeploymentPublicationState? publicationState = null,
        DeploymentSchedule? schedule = null,
        bool clearSchedule = false,
        RuntimeState? runtime = null,
        string? supersededBy = null,
        DateTimeOffset? activatedAt = null,
        DateTimeOffset? retiredAt = null,
        string? failureReason = null,
        DateTimeOffset? now = null,
        string? reason = null,
        OperationAuditInfo? audit = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var nextRolloutState = rolloutState ?? RolloutState;
        var nextStep = currentRolloutStep ?? CurrentRolloutStep;
        return this with
        {
            Status = toStatus,
            PublicationState = publicationState ?? PublicationState,
            RolloutState = nextRolloutState,
            CurrentRolloutStep = nextStep,
            Schedule = clearSchedule ? null : (schedule ?? Schedule),
            Runtime = runtime ?? Runtime,
            SupersededByDeploymentId = supersededBy ?? SupersededByDeploymentId,
            ActivatedAt = activatedAt ?? ActivatedAt,
            RetiredAt = retiredAt ?? RetiredAt,
            FailureReason = toStatus == DeploymentStatus.Failed ? failureReason ?? FailureReason : null,
            UpdatedAt = at,
            Transitions = AppendTransition(
                from: Status,
                to: toStatus,
                at: at,
                rolloutState: nextRolloutState,
                reason: reason,
                audit: audit)
        };
    }

    private List<DeploymentTransition> AppendTransition(
        DeploymentStatus from,
        DeploymentStatus to,
        DateTimeOffset at,
        RolloutState? rolloutState,
        string? reason,
        OperationAuditInfo? audit)
    {
        var entry = new DeploymentTransition
        {
            From = from,
            To = to,
            At = at,
            RolloutState = rolloutState,
            Reason = reason,
            Audit = audit ?? Audit
        };

        var next = new List<DeploymentTransition>(Transitions.Count + 1);
        next.AddRange(Transitions);
        next.Add(entry);
        return next;
    }

    private RolloutState? ResolveTerminalRolloutState()
        => Status == DeploymentStatus.RollingOut && RolloutState == Domain.RolloutState.InProgress
            ? Domain.RolloutState.Cancelled
            : null;

    private void EnsureValidRolloutStep(int? step)
    {
        if (step is null)
        {
            if (Status == DeploymentStatus.RollingOut)
            {
                throw new ArgumentNullException(
                    nameof(step),
                    "Rollout step is required when re-entering RollingOut; step must strictly advance past the current step.");
            }

            return;
        }

        if (Rollout.Strategy != RolloutStrategy.Canary)
        {
            throw new InvalidOperationException(
                "An explicit rollout step is only valid for canary rollouts.");
        }

        if (step < 0 || step >= Rollout.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                $"Rollout step is outside the plan's range [0, {Rollout.Steps.Count - 1}].");
        }

        if (Status == DeploymentStatus.RollingOut && CurrentRolloutStep is int current && step.Value <= current)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                $"Rollout step must strictly advance; current step is {current}.");
        }
    }

    private void EnsureTransitionAllowed(
        DeploymentStatus target,
        params ReadOnlySpan<DeploymentStatus> allowedFrom)
    {
        for (var i = 0; i < allowedFrom.Length; i++)
        {
            if (allowedFrom[i] == Status)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Cannot transition deployment from {Status} to {target}.");
    }
}
