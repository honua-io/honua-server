// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Canonical branch-versioning manager (#1272 Track B). Owns the lifecycle of gdb versions
/// (create/delete/alter/list), version read/edit sessions (which set the version lock and the
/// transaction-scoped version GUC), and the reconcile/post merge workflow. A null
/// <see cref="VersionContext"/> always denotes the DEFAULT version and must be handled exactly as the
/// non-versioned base path so existing reads/writes and CITE conformance stay byte-identical.
/// </summary>
/// <remarks>
/// Branch versioning is Postgres-only and gated behind the Enterprise entitlement; other providers
/// return not-supported. Reconcile detects conflicts between the version and DEFAULT since the merge
/// base (reusing the disconnected-sync conflict classification), applies #371's auto-policies, and
/// blocks <see cref="PostAsync"/> while unresolved conflicts remain. Post replays the version's net
/// changes onto DEFAULT through the shared edit pipeline in one transaction and advances the
/// generation. Large reconcile/post runs are expected to execute through the canonical job runtime,
/// and the version is locked (Redis-backed) for the duration of a reconcile or post.
/// </remarks>
public interface IVersionManager
{
    /// <summary>Whether this provider supports branch versioning. Non-Postgres providers return false.</summary>
    bool SupportsVersioning { get; }

    /// <summary>Creates a new branch version.</summary>
    /// <param name="request">Create request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created version.</returns>
    Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a branch version. DEFAULT cannot be deleted.</summary>
    /// <param name="versionId">Version to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the version existed and was deleted.</returns>
    Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Alters a version's mutable metadata.</summary>
    /// <param name="request">Alter request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated version, or null when not found.</returns>
    Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists all branch versions (excluding the implicit DEFAULT).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The known branch versions.</returns>
    Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a <c>gdbVersion</c> parameter to a <see cref="VersionContext"/>. An absent/empty value
    /// or the DEFAULT sentinel (e.g. <c>SDE.DEFAULT</c>) resolves to <see cref="VersionContext.Default"/>;
    /// an unknown name yields null so the caller can return a not-found error.
    /// </summary>
    /// <param name="gdbVersion">Requested version identity, or null/empty for DEFAULT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved context, or null when the named version does not exist.</returns>
    Task<VersionContext?> ResolveAsync(string? gdbVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a version against DEFAULT, detecting conflicts since the merge base. Disjoint
    /// field-level edits auto-merge; only genuinely-overlapping edits (overlapping fields,
    /// geometry-vs-geometry, or update-vs-delete) become conflicts. When <paramref name="policy"/> is
    /// a deterministic policy (#371) each conflict is auto-resolved so the version may be posted; when
    /// <see cref="VersionReconcilePolicy.None"/> the unresolved conflicts are persisted for manual
    /// review and block <c>Post</c>.
    /// </summary>
    /// <param name="versionId">Version to reconcile.</param>
    /// <param name="policy">Auto-resolution policy; <see cref="VersionReconcilePolicy.None"/> for manual review.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reconcile result.</returns>
    Task<VersionReconcileResult> ReconcileAsync(
        Guid versionId,
        VersionReconcilePolicy policy = VersionReconcilePolicy.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current set of pending (unresolved) conflicts persisted for a version, each with the
    /// three-way base/DEFAULT/version images and per-field diffs for a manual-resolution UI (#371). An
    /// empty result means there are no pending conflicts and the version may be posted.
    /// </summary>
    /// <param name="versionId">Version whose pending conflicts to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pending conflicts.</returns>
    Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies operator-submitted resolution choices to a version's pending conflicts (#371). Each
    /// choice rewrites the branch overlay row to the selected side (version/DEFAULT/base) and clears
    /// the persisted pending conflict, after which <c>Post</c> may proceed once no pending conflicts
    /// remain.
    /// </summary>
    /// <param name="versionId">Version whose conflicts to resolve.</param>
    /// <param name="resolutions">Per-feature resolution choices.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolution outcome (resolved/remaining counts and whether post may proceed).</returns>
    Task<VersionConflictResolutionResult> ResolveConflictsAsync(
        Guid versionId,
        IReadOnlyList<VersionConflictResolution> resolutions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a reconciled version's net changes onto DEFAULT. Refused while unresolved conflicts
    /// remain (the version must be reconciled clean first).
    /// </summary>
    /// <param name="versionId">Version to post.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The post result.</returns>
    Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default);
}
