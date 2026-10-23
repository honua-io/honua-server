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
/// Which revision a metadata-release smoke check exercises, and why.
/// </summary>
public enum MetadataReleaseSmokePhase
{
    /// <summary>
    /// The staged candidate, explicitly, before activation. Canonical readers are still on the
    /// prior revision.
    /// </summary>
    Candidate,

    /// <summary>
    /// The live revision after activation (post-activation regression check).
    /// </summary>
    Activated,

    /// <summary>
    /// The live revision after rollback, proving recovered functional behavior.
    /// </summary>
    Recovered
}

/// <summary>
/// Identifies the revision a smoke check must exercise and the behavior it must observe.
/// </summary>
public sealed record MetadataReleaseSmokeRequest
{
    /// <summary>
    /// Lifecycle moment of the check.
    /// </summary>
    public required MetadataReleaseSmokePhase Phase { get; init; }

    /// <summary>
    /// Retained revision to exercise. The check never reads "whatever is current".
    /// </summary>
    public required long Revision { get; init; }

    /// <summary>
    /// Revision whose authorization behavior the exercised revision must preserve. Null skips the
    /// comparison (for example when no prior revision is retained).
    /// </summary>
    public long? BaselineRevision { get; init; }

    /// <summary>
    /// Whether the plan's new field must be present (true) or absent (false) in the exercised schema.
    /// </summary>
    public required bool ExpectNewField { get; init; }
}

/// <summary>
/// Result of running a metadata-release smoke check.
/// </summary>
public sealed record MetadataReleaseSmokeResult
{
    /// <summary>
    /// Whether every check passed: schema expectation, bindings, rendering references,
    /// authorization parity and the canonical query.
    /// </summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// Number of rows returned by the canonical query probe.
    /// </summary>
    public long RowCount { get; init; }

    /// <summary>
    /// Whether the exercised revision's schema includes the plan's new field.
    /// </summary>
    public bool NewFieldPresent { get; init; }

    /// <summary>
    /// Safe operator-facing message describing the smoke outcome.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Result of preparing a graph from a script: the transformed graph and the operations that
/// actually changed it (idempotent no-ops are excluded).
/// </summary>
public sealed record MetadataReleaseScriptResult
{
    /// <summary>
    /// Transformed graph. Not persisted by the script executor.
    /// </summary>
    public required MetadataV2Graph Graph { get; init; }

    /// <summary>
    /// Operations that changed the graph.
    /// </summary>
    public IReadOnlyList<MetadataReleaseScriptOperation> AppliedOperations { get; init; } = Array.Empty<MetadataReleaseScriptOperation>();
}

/// <summary>
/// Raised when a candidate cannot be prepared (or an inverse cannot be applied) without breaking
/// the protected-change contract. Nothing live has been mutated when this is thrown.
/// </summary>
public sealed class MetadataReleasePreparationException : Exception
{
    /// <summary>
    /// Creates a preparation failure with a stable blocker code.
    /// </summary>
    public MetadataReleasePreparationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Stable, machine-readable blocker code.
    /// </summary>
    public string Code { get; }
}

/// <summary>
/// Outcome of a conditional candidate activation.
/// </summary>
public enum MetadataReleaseActivationOutcome
{
    /// <summary>
    /// The candidate became current in one atomic pointer move.
    /// </summary>
    Activated,

    /// <summary>
    /// The candidate was already current (a resumed activation).
    /// </summary>
    AlreadyActive,

    /// <summary>
    /// A newer update became current first; nothing was overwritten.
    /// </summary>
    Conflict
}

/// <summary>
/// Result of a conditional candidate activation.
/// </summary>
public sealed record MetadataReleaseActivationResult
{
    /// <summary>
    /// Activation outcome.
    /// </summary>
    public required MetadataReleaseActivationOutcome Outcome { get; init; }

    /// <summary>
    /// Revision current after the attempt.
    /// </summary>
    public required long CurrentRevision { get; init; }

    /// <summary>
    /// ETag current after the attempt.
    /// </summary>
    public required string CurrentEtag { get; init; }
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
/// Applies the additive forward schema script, or the inverse of the operations a release owns,
/// to a graph in memory. Never persists: staging and activation belong to
/// <see cref="IMetadataReleaseActivator"/>.
/// </summary>
public interface IMetadataReleaseScriptExecutor
{
    /// <summary>
    /// Applies the plan's forward operations to <paramref name="baseline"/>. Idempotent: an add of
    /// an identical existing field is a no-op and is not reported as applied.
    /// </summary>
    /// <exception cref="MetadataReleasePreparationException">The target resource is missing or an
    /// existing field conflicts with the declared definition.</exception>
    MetadataReleaseScriptResult PrepareForward(
        MetadataReleaseExecutionPlan plan,
        MetadataV2Graph baseline);

    /// <summary>
    /// Reverts exactly <paramref name="ownedOperations"/> on <paramref name="current"/>, leaving
    /// every other resource, field and service untouched. Idempotent: an already-reverted change is
    /// a no-op.
    /// </summary>
    /// <exception cref="MetadataReleasePreparationException">An owned field was changed by someone
    /// else since activation, so reverting it would destroy an unrelated update.</exception>
    MetadataReleaseScriptResult PrepareInverse(
        IReadOnlyList<MetadataReleaseScriptOperation> ownedOperations,
        MetadataV2Graph current);
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
/// Stages, activates and reverts Metadata v2 revisions for the metadata-release lifecycle through
/// the canonical graph store. Reads always go to the persisted store, never a cached snapshot, so
/// optimistic-concurrency preconditions are exact.
/// </summary>
public interface IMetadataReleaseActivator
{
    /// <summary>
    /// Returns the persisted current revision.
    /// </summary>
    Task<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a retained revision, or null when it is no longer retained.
    /// </summary>
    Task<MetadataV2GraphSnapshot?> GetRevisionAsync(long revision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="candidate"/> as an immutable retained revision without moving the
    /// current pointer.
    /// </summary>
    /// <exception cref="MetadataReleasePreparationException">The graph store cannot stage revisions.</exception>
    Task<MetadataV2GraphSnapshot> StageAsync(MetadataV2Graph candidate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards a staged revision that never became current. Idempotent.
    /// </summary>
    Task DiscardAsync(long revision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="revision"/> current only if <paramref name="expectedCurrentEtag"/> is
    /// still current. A newer update is reported as <see cref="MetadataReleaseActivationOutcome.Conflict"/>,
    /// never overwritten.
    /// </summary>
    Task<MetadataReleaseActivationResult> ActivateAsync(
        long revision,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a reverted graph as the new current revision only if
    /// <paramref name="expectedCurrentEtag"/> is still current.
    /// </summary>
    /// <exception cref="Honua.Core.Features.Metadata.Abstractions.MetadataV2GraphConcurrencyException">
    /// Another writer advanced the current revision.</exception>
    Task<MetadataV2GraphSnapshot> CommitRevertAsync(
        MetadataV2Graph reverted,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a metadata-release smoke check against an explicit retained revision.
/// </summary>
public interface IMetadataReleaseSmokeChecker
{
    /// <summary>
    /// Exercises the requested revision through the canonical query pipeline and checks schema,
    /// bindings, rendering references and authorization parity with the baseline revision.
    /// </summary>
    Task<MetadataReleaseSmokeResult> RunAsync(
        MetadataReleaseExecutionPlan plan,
        MetadataReleaseSmokeRequest request,
        CancellationToken cancellationToken = default);
}
