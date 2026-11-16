// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public enum CoordinatedStepOutcome
{
    Pending,

    Succeeded,

    RolledBack,

    Failed
}

public sealed record CoordinatedStepResult
{
    public required CoordinatedStepOutcome Outcome { get; init; }

    public string? ChildOperationId { get; init; }

    public string? Detail { get; init; }

    public string? ObservedRevision { get; init; }

    public string? ExpectedRevision { get; init; }
}

public interface ICoordinatedContainerStepExecutor
{
    Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<CoordinatedStepResult> ObserveAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);

    Task<CoordinatedStepResult> RollbackAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);
}

public interface ICoordinatedMetadataStepExecutor
{
    Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<CoordinatedStepResult> ObserveAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);
}

public interface ICoordinatedReleaseReconciler
{
    Task ReconcileCoordinatedReleaseAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
