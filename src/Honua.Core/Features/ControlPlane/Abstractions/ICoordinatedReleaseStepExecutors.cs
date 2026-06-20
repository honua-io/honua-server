// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Terminal outcome of observing a composed child lifecycle step (container rollout or
/// metadata+schema). The coordinated conductor advances only when a step reaches a terminal state and
/// triggers the coordinated rollback when a step fails.
/// </summary>
public enum CoordinatedStepOutcome
{
    /// <summary>
    /// The step is still progressing; the conductor should re-observe on its next cycle.
    /// </summary>
    Pending,

    /// <summary>
    /// The step reached its terminal success state.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The step failed; the conductor must trigger the coordinated rollback.
    /// </summary>
    Failed
}

/// <summary>
/// Result of starting or observing a composed coordinated-release step. Carries the child operation id
/// the conductor records so it can observe and roll the step back through the same composed service.
/// </summary>
public sealed record CoordinatedStepResult
{
    /// <summary>
    /// Terminal/intermediate outcome of the step.
    /// </summary>
    public required CoordinatedStepOutcome Outcome { get; init; }

    /// <summary>
    /// Child operation id the composed lifecycle created for this step, when one exists.
    /// </summary>
    public string? ChildOperationId { get; init; }

    /// <summary>
    /// Operator-facing detail for the step.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Composes the existing container deploy/rollout lifecycle as a single coordinated-release step.
/// Implementations are thin adapters over the deploy workflow service; they do not reimplement the
/// rollout, canary, or backend dispatch behavior.
/// </summary>
public interface ICoordinatedContainerStepExecutor
{
    /// <summary>
    /// Starts the container rollout for the coordinated release and returns the child deploy operation
    /// id. Idempotent: a re-run with an existing child id observes rather than re-submits.
    /// </summary>
    Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes the in-flight container rollout child operation and reports its terminal outcome.
    /// </summary>
    Task<CoordinatedStepResult> ObserveAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls the container rollout back through the existing deploy rollback path. Idempotent.
    /// </summary>
    Task RollbackAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes the existing additive metadata-release lifecycle (its DB ScriptMigration + metadata-v2
/// activation) as a single coordinated-release step. Implementations are thin adapters over the
/// metadata-release control service and reconciler; they do not reimplement the schema mutation,
/// activation, or reversible inverse.
/// </summary>
public interface ICoordinatedMetadataStepExecutor
{
    /// <summary>
    /// Starts the additive metadata-release lifecycle for the coordinated release and returns the child
    /// metadata-release operation id. Idempotent on the coordinated package id.
    /// </summary>
    Task<CoordinatedStepResult> StartAsync(
        CoordinatedReleaseContext context,
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drives and observes the in-flight metadata-release child operation and reports its terminal
    /// outcome (Succeeded once the metadata reconciler reaches Complete, Failed on smoke/rollback).
    /// </summary>
    Task<CoordinatedStepResult> ObserveAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the reversible rollback of the metadata-release child operation (reactivate the prior
    /// revision and execute the inverse schema script). Idempotent.
    /// </summary>
    Task RollbackAsync(
        string childOperationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Conducts a coordinated platform-upgrade release by advancing the ordered steps and triggering the
/// single coordinated rollback. Mirrors the per-cycle, single-advance, leased contract of the deploy
/// and metadata-release reconcilers so the background loop re-enters and resumes deterministically.
/// </summary>
public interface ICoordinatedReleaseReconciler
{
    /// <summary>
    /// Advances the coordinated release by one step, or unwinds completed steps in reverse when a
    /// rollback is in flight.
    /// </summary>
    Task ReconcileCoordinatedReleaseAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
