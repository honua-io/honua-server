// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Result of running the metadata-release preflight compatibility/sync-check gate.
/// </summary>
public sealed record MetadataReleasePreflightResult
{
    /// <summary>
    /// Whether the release is cleared to advance past Preflight.
    /// </summary>
    public required bool CanProceed { get; init; }

    /// <summary>
    /// Rollback classification derived from the compatibility analysis. Determines whether the
    /// additive reversible path is eligible or a snapshot is required.
    /// </summary>
    public required MetadataRollbackReadinessClassification RollbackClassification { get; init; }

    /// <summary>
    /// Safe operator-facing reason for the classification or block.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Blocking reason codes that prevent the release from proceeding.
    /// </summary>
    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Result of running the post-publish layer Smoke check.
/// </summary>
public sealed record MetadataReleaseSmokeResult
{
    /// <summary>
    /// Whether the smoke check passed: the canonical query returned rows and the activated schema
    /// includes the newly added field.
    /// </summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// Number of rows returned by the canonical query probe.
    /// </summary>
    public long RowCount { get; init; }

    /// <summary>
    /// Whether the activated revision's schema includes the expected new field.
    /// </summary>
    public bool NewFieldPresent { get; init; }

    /// <summary>
    /// Safe operator-facing message describing the smoke outcome.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Preflight gate for a metadata-release lifecycle. Runs the shared compatibility/sync-check
/// analyzer and classifies the rollback so the reconciler can refuse snapshot-required releases.
/// </summary>
public interface IMetadataReleasePreflightGate
{
    /// <summary>
    /// Runs the compatibility/sync-check gate for the additive release plan.
    /// </summary>
    Task<MetadataReleasePreflightResult> EvaluateAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes the additive forward schema script and its reversible inverse.
/// </summary>
public interface IMetadataReleaseScriptExecutor
{
    /// <summary>
    /// Applies the additive forward schema operations (for example add-nullable-column).
    /// Idempotent: re-applying an already-applied additive change must succeed.
    /// </summary>
    Task ApplyForwardAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the reversible inverse (for example drop-added-column). Idempotent.
    /// </summary>
    Task ApplyInverseAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches the optional ETL/data-populate workload as a control-plane job and observes it.
/// </summary>
public interface IMetadataReleaseDataJobDispatcher
{
    /// <summary>
    /// Dispatches the data-populate job for the plan and waits for a terminal job state.
    /// Returns true on success, false when no workload is declared, throws on job failure.
    /// </summary>
    Task<bool> DispatchAndAwaitAsync(
        MetadataReleaseExecutionPlan plan,
        string operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Activates and reactivates Metadata v2 revisions for the metadata-release lifecycle.
/// </summary>
public interface IMetadataReleaseActivator
{
    /// <summary>
    /// Captures the prior current revision so a reversible rollback can reactivate it. Returns null
    /// when no current revision exists yet.
    /// </summary>
    Task<long?> GetCurrentRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the new revision for the additive release (MetadataApply stage).
    /// </summary>
    Task ActivateNewRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reactivates a prior revision during reversible rollback.
    /// </summary>
    Task ReactivateRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        long revision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the post-publish layer Smoke check through the canonical query pipeline.
/// </summary>
public interface IMetadataReleaseSmokeChecker
{
    /// <summary>
    /// Queries the just-published layer through the shared canonical query path and asserts rows
    /// return and the activated schema includes the new field.
    /// </summary>
    Task<MetadataReleaseSmokeResult> RunAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default);
}
