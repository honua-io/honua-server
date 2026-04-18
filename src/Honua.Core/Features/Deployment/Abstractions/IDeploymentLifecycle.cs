// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;

namespace Honua.Core.Features.Deployment.Abstractions;

/// <summary>
/// Store for persisting and querying deployment lifecycle records.
/// </summary>
public interface IDeploymentStore
{
    /// <summary>
    /// Attempts to create a new deployment record.
    /// </summary>
    /// <returns>True when created; false when a deployment with the same ID already exists.</returns>
    Task<bool> TryCreateAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a deployment record by identifier.
    /// </summary>
    Task<Domain.Deployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest deployment record state, including any appended transitions.
    /// </summary>
    Task SetAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments whose status is not <see cref="DeploymentStatus.Retired"/> or
    /// <see cref="DeploymentStatus.Superseded"/>.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments backed by a specific promoted artifact.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListBySourceAsync(
        DeploymentSourceKind sourceKind,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments mapped to a specific hosting target.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Schedules deployments for future publication and surfaces due deployments to the
/// deployment executor.
/// </summary>
public interface IDeploymentScheduler
{
    /// <summary>
    /// Schedules the deployment for activation at the given schedule. The deployment is
    /// transitioned to <see cref="DeploymentStatus.Scheduled"/> and persisted.
    /// </summary>
    Task<Domain.Deployment> ScheduleAsync(
        Domain.Deployment deployment,
        DeploymentSchedule schedule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a previously scheduled deployment before it has been provisioned.
    /// </summary>
    Task<Domain.Deployment> CancelScheduleAsync(
        string deploymentId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists scheduled deployments whose publication time is at or before <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListDueAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes deployment lifecycle transitions for provisioning, rollout, and promotion.
/// </summary>
public interface IDeploymentExecutor
{
    /// <summary>
    /// Moves the deployment from <see cref="DeploymentStatus.Draft"/> or
    /// <see cref="DeploymentStatus.Scheduled"/> into <see cref="DeploymentStatus.Provisioning"/>,
    /// performing any hosting-specific preparation.
    /// </summary>
    Task<Domain.Deployment> ProvisionAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances a rollout toward <see cref="DeploymentStatus.Active"/>. For canary rollouts
    /// this advances the current rollout step; for immediate and blue/green rollouts it
    /// cuts over to serving.
    /// </summary>
    Task<Domain.Deployment> PromoteAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses a rollout that has not yet been promoted, transitioning the deployment
    /// through a <see cref="RolloutState.RolledBack"/> state.
    /// </summary>
    Task<Domain.Deployment> RollbackAsync(
        Domain.Deployment deployment,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime inspection surface exposing observed state and lifecycle history for
/// deployments without reopening deployment semantics.
/// </summary>
public interface IDeploymentRuntimeInspector
{
    /// <summary>
    /// Returns the latest observed runtime state for a deployment, or null when the
    /// deployment is unknown.
    /// </summary>
    Task<RuntimeState?> GetRuntimeStateAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ordered audit trail of lifecycle transitions for a deployment.
    /// </summary>
    Task<IReadOnlyList<DeploymentTransition>> GetTransitionHistoryAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);
}
