// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Progress tracking for a deployment operation, implementing the unified operation
/// progress interface.
/// </summary>
public sealed record DeploymentProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this deployment operation.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Current deployment lifecycle status.
    /// </summary>
    public required DeploymentStatus DeploymentStatus { get; init; }

    /// <summary>
    /// Identifier of the deployment being tracked.
    /// </summary>
    public string? DeploymentId { get; init; }

    /// <summary>
    /// Observed rollout state.
    /// </summary>
    public RolloutState? RolloutState { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// When the operation started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the operation completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Non-fatal warnings encountered during the operation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Unified operation status projected from <see cref="DeploymentStatus"/>.
    /// Terminal lifecycle statuses (<see cref="DeploymentStatus.Active"/>,
    /// <see cref="DeploymentStatus.Superseded"/>, <see cref="DeploymentStatus.Retired"/>)
    /// map to <see cref="OperationStatus.Completed"/> so eval and automation surfaces
    /// observe the deployment operation as finished once it has reached a serving or
    /// wound-down state.
    /// </summary>
    public OperationStatus Status => DeploymentStatus switch
    {
        DeploymentStatus.Draft => OperationStatus.Queued,
        DeploymentStatus.Scheduled => OperationStatus.Queued,
        DeploymentStatus.Provisioning => OperationStatus.Processing,
        DeploymentStatus.RollingOut => OperationStatus.Processing,
        DeploymentStatus.Active => OperationStatus.Completed,
        DeploymentStatus.Superseded => OperationStatus.Completed,
        DeploymentStatus.Retired => OperationStatus.Completed,
        DeploymentStatus.Failed => OperationStatus.Failed,
        DeploymentStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    string IOperationProgress.OperationId => OperationId;

    OperationType IOperationProgress.Type => OperationType.Deployment;

    /// <inheritdoc />
    /// <remarks>
    /// Normalizes <see cref="RolloutState"/> to <see cref="Domain.RolloutState.Cancelled"/> so a
    /// cancelled progress record never leaks a stale <see cref="Domain.RolloutState.InProgress"/>
    /// value into eval, automation, or dashboard surfaces that project this record.
    /// </remarks>
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            DeploymentStatus = DeploymentStatus.Cancelled,
            RolloutState = Domain.RolloutState.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Creates an initial progress record for a new deployment operation.
    /// </summary>
    public static DeploymentProgress CreateInitial(string operationId, string deploymentId)
        => new()
        {
            OperationId = operationId,
            DeploymentStatus = DeploymentStatus.Draft,
            DeploymentId = deploymentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Initializing"
        };

    /// <summary>
    /// Creates a progress record for a provisioning deployment operation.
    /// </summary>
    public static DeploymentProgress CreateProvisioning(string operationId, string deploymentId)
        => new()
        {
            OperationId = operationId,
            DeploymentStatus = DeploymentStatus.Provisioning,
            DeploymentId = deploymentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Provisioning"
        };

    /// <summary>
    /// Creates a progress record for a rollout in progress.
    /// </summary>
    public static DeploymentProgress CreateRollingOut(string operationId, string deploymentId, RolloutState rolloutState)
        => new()
        {
            OperationId = operationId,
            DeploymentStatus = DeploymentStatus.RollingOut,
            DeploymentId = deploymentId,
            RolloutState = rolloutState,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Rolling out"
        };
}
