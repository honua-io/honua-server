// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Deployment.Domain;

public sealed record DeploymentProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string OperationId { get; init; }

    public required DeploymentStatus DeploymentStatus { get; init; }

    public string? DeploymentId { get; init; }

    public RolloutState? RolloutState { get; init; }

    public double? PercentComplete { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

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

    // A cancelled progress record marks the rollout cancelled so eval and dashboards stop projecting the in-progress state.
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            DeploymentStatus = DeploymentStatus.Cancelled,
            RolloutState = Domain.RolloutState.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    public static DeploymentProgress CreateInitial(string operationId, string deploymentId)
        => new()
        {
            OperationId = operationId,
            DeploymentStatus = DeploymentStatus.Draft,
            DeploymentId = deploymentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Initializing"
        };

    public static DeploymentProgress CreateProvisioning(string operationId, string deploymentId)
        => new()
        {
            OperationId = operationId,
            DeploymentStatus = DeploymentStatus.Provisioning,
            DeploymentId = deploymentId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Provisioning"
        };

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
