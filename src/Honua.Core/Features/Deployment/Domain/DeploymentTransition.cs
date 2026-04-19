// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Durable audit entry describing a single deployment lifecycle transition. Transitions
/// are appended in order on every status change and provide the deterministic, auditable
/// trail consumed by eval and automation surfaces.
/// </summary>
public sealed record DeploymentTransition
{
    /// <summary>
    /// Status the deployment was in before this transition.
    /// </summary>
    public required DeploymentStatus From { get; init; }

    /// <summary>
    /// Status the deployment entered as a result of this transition.
    /// </summary>
    public required DeploymentStatus To { get; init; }

    /// <summary>
    /// When the transition was recorded.
    /// </summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Rollout state observed at the moment of the transition, when the transition is
    /// associated with rollout progress.
    /// </summary>
    public RolloutState? RolloutState { get; init; }

    /// <summary>
    /// Optional free-form reason recorded by the transition source.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Operator identity and request metadata captured at the moment of the transition.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();
}
