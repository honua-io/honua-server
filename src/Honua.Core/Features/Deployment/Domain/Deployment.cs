// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Durable deployment record that ties a promoted publish or package artifact to a
/// hosted deployment target. Deployment lifecycle transitions are deterministic,
/// auditable, and recorded in <see cref="Transitions"/>; publish and package semantics
/// remain owned by their originating lifecycles.
/// </summary>
public sealed record Deployment
{
    /// <summary>
    /// Stable identifier for this deployment.
    /// </summary>
    public required string DeploymentId { get; init; }

    /// <summary>
    /// Promoted artifact backing this deployment.
    /// </summary>
    public required DeploymentSource Source { get; init; }

    /// <summary>
    /// Hosting target on which the deployment is served.
    /// </summary>
    public required DeploymentTarget Target { get; init; }

    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    public required DeploymentStatus Status { get; init; }

    /// <summary>
    /// Operator-facing publication state tracking whether the deployment is visible on
    /// its routable surface.
    /// </summary>
    public required DeploymentPublicationState PublicationState { get; init; }

    /// <summary>
    /// Rollout plan governing how this deployment reaches its final serving weight.
    /// Immutable once the deployment is created.
    /// </summary>
    public RolloutPlan Rollout { get; init; } = RolloutPlan.Immediate();

    /// <summary>
    /// Observed rollout state.
    /// </summary>
    public RolloutState RolloutState { get; init; } = Domain.RolloutState.Pending;

    /// <summary>
    /// Index of the current canary step (0-based) when <see cref="RolloutPlan.Strategy"/>
    /// is <see cref="RolloutStrategy.Canary"/>. Null for non-canary rollouts or before
    /// the first step has begun.
    /// </summary>
    public int? CurrentRolloutStep { get; init; }

    /// <summary>
    /// Publication schedule, when the deployment is time-gated.
    /// </summary>
    public DeploymentSchedule? Schedule { get; init; }

    /// <summary>
    /// Observed runtime state.
    /// </summary>
    public RuntimeState Runtime { get; init; } = RuntimeState.Unknown();

    /// <summary>
    /// Operator identity and request metadata captured when the deployment was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// When the deployment was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the deployment record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// When the deployment first reached <see cref="DeploymentStatus.Active"/>. Null while
    /// the deployment has not yet been published.
    /// </summary>
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>
    /// When the deployment was retired or superseded. Null while still live.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>
    /// Reason for entering <see cref="DeploymentStatus.Failed"/> when applicable.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Identifier of the deployment that superseded this one. Set on transition to
    /// <see cref="DeploymentStatus.Superseded"/> and preserved if the deployment is later
    /// retired, so the durable record retains the replacement link.
    /// </summary>
    public string? SupersededByDeploymentId { get; init; }

    /// <summary>
    /// Identifier of an associated control-plane workflow operation when provider-backed
    /// deploy orchestration is used for this deployment.
    /// </summary>
    public string? ControlPlaneOperationId { get; init; }

    /// <summary>
    /// Ordered audit trail of lifecycle transitions. Entries are append-only and
    /// reflect every status change; runtime-only updates do not append transitions.
    /// </summary>
    public IReadOnlyList<DeploymentTransition> Transitions { get; init; } = [];

    /// <summary>
    /// Creates a new draft deployment targeting the given source and hosting target.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rollout"/> uses
    /// <see cref="RolloutStrategy.Scheduled"/> without a matching <paramref name="schedule"/>.
    /// A schedule-gated rollout must carry a publication schedule for the lifecycle to honor
    /// its gate; otherwise the deployment could activate immediately and the strategy would be
    /// declarative-only.</exception>
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

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Scheduled"/>, attaching
    /// the publication schedule that gates activation.
    /// </summary>
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

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Provisioning"/>.
    /// </summary>
    public Deployment WithProvisioning(string? reason = null, OperationAuditInfo? audit = null)
    {
        EnsureTransitionAllowed(
            DeploymentStatus.Provisioning,
            DeploymentStatus.Draft,
            DeploymentStatus.Scheduled);
        return ApplyTransition(DeploymentStatus.Provisioning, reason: reason, audit: audit);
    }

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.RollingOut"/>, optionally
    /// setting the initial rollout step for canary rollouts. When the deployment is already
    /// rolling out, an explicit <paramref name="step"/> must strictly advance past the
    /// current step.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="step"/> is
    /// outside the plan's range, or when the deployment is already rolling out and
    /// <paramref name="step"/> does not strictly advance past the current step.</exception>
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

    /// <summary>
    /// Advances the current canary step without changing the overall deployment status.
    /// Only valid while in <see cref="DeploymentStatus.RollingOut"/> with a canary rollout.
    /// Steps must strictly advance: repeating or regressing is rejected so the rollout
    /// audit trail reflects forward progression.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the rollout is not a canary rollout or the deployment is not rolling out.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="step"/> is outside the plan's step range, or does not strictly advance past the current step.</exception>
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

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Active"/>, marking the
    /// rollout as promoted and the publication state as published. When a
    /// <see cref="Schedule"/> is attached, activation is gated to the publication window:
    /// the deployment must be due and must not have passed its auto-retire time. A
    /// <see cref="RolloutStrategy.Scheduled"/> rollout always requires a schedule; activation
    /// without one is rejected to keep the declared strategy load-bearing. For
    /// <see cref="RolloutStrategy.Canary"/> rollouts, activation from
    /// <see cref="DeploymentStatus.RollingOut"/> requires the current step to match the
    /// final step of the plan so that canary promotion honors its declared progression.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a schedule is attached and
    /// the current time is outside its publish window, when the rollout strategy is
    /// <see cref="RolloutStrategy.Scheduled"/> but no schedule is attached, or when a
    /// canary rollout is activated before reaching its final step.</exception>
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

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Superseded"/>, recording
    /// the successor that replaced it.
    /// </summary>
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
            publicationState: DeploymentPublicationState.Unpublished,
            clearSchedule: true,
            supersededBy: successorDeploymentId,
            retiredAt: RetiredAt ?? now,
            now: now,
            reason: reason,
            audit: audit);
    }

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Retired"/>.
    /// </summary>
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
            publicationState: DeploymentPublicationState.Retired,
            clearSchedule: true,
            retiredAt: RetiredAt ?? now,
            now: now,
            reason: reason,
            audit: audit);
    }

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Failed"/>, recording the
    /// failure reason and marking the rollout as failed. Only valid before the deployment
    /// has reached a terminal state; an <see cref="DeploymentStatus.Active"/> deployment
    /// that needs to wind down must be retired or superseded instead.
    /// </summary>
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

    /// <summary>
    /// Transitions the deployment to <see cref="DeploymentStatus.Cancelled"/>, marking the
    /// rollout as cancelled. Clears any pending schedule and marks the deployment as
    /// unpublished so a cancelled record never carries stale <see cref="DeploymentPublicationState.Scheduled"/>
    /// metadata or a due <see cref="Schedule"/> that will never be honored. Only valid before
    /// the deployment has reached a terminal state or begun serving traffic.
    /// </summary>
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

    /// <summary>
    /// Reverses the rollout, leaving the deployment in <see cref="DeploymentStatus.Failed"/>
    /// with a <see cref="Domain.RolloutState.RolledBack"/> state. Callers are expected to
    /// subsequently retire the deployment or replace it with a rollback successor. Only valid
    /// while the rollout has not yet been promoted; an <see cref="DeploymentStatus.Active"/>
    /// deployment must be superseded or retired rather than rolled back through this helper.
    /// </summary>
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

    /// <summary>
    /// Updates observed runtime state without changing the deployment's lifecycle status
    /// or appending a transition. Runtime state is owned by the runtime inspector and
    /// evolves independently of the operator-driven lifecycle.
    /// </summary>
    public Deployment WithRuntime(RuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return this with { Runtime = runtime, UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Associates the deployment with a control-plane workflow operation identifier when
    /// provider-backed deploy orchestration is being used.
    /// </summary>
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

    private void EnsureValidRolloutStep(int? step)
    {
        if (step is null)
        {
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
