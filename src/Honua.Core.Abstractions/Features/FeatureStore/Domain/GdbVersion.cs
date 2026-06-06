// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Access level of a branch version. Mirrors the Esri version access taxonomy but is exposed as a
/// Honua product contract (#1272 Track B).
/// </summary>
public enum VersionAccess
{
    /// <summary>Only the owner may read or edit the version.</summary>
    Private = 0,

    /// <summary>Anyone may read; only the owner may edit.</summary>
    Protected = 1,

    /// <summary>Anyone may read or edit the version.</summary>
    Public = 2,
}

/// <summary>
/// Lifecycle state of a branch version.
/// </summary>
public enum VersionState
{
    /// <summary>Version is editable and queryable.</summary>
    Active = 0,

    /// <summary>A reconcile is in progress; the version is locked.</summary>
    Reconciling = 1,

    /// <summary>A post is in progress; the version is locked.</summary>
    Posting = 2,

    /// <summary>Version has been deleted.</summary>
    Deleted = 3,
}

/// <summary>
/// A branch version: a named branch off DEFAULT that accumulates isolated edits which are later
/// reconciled against and posted back to DEFAULT. The DEFAULT version is implicit (represented by a
/// null version context everywhere) and is never materialized as a <see cref="GdbVersion"/> row, so
/// existing non-versioned read/write paths stay byte-identical (#1272 Track B).
/// </summary>
public readonly record struct GdbVersion
{
    /// <summary>Unique version identifier.</summary>
    public required Guid VersionId { get; init; }

    /// <summary>Esri-style <c>owner.name</c> version identity.</summary>
    public required string VersionName { get; init; }

    /// <summary>Version owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Parent version this branch was created from; null when branched from DEFAULT.</summary>
    public Guid? ParentVersion { get; init; }

    /// <summary>Access level.</summary>
    public required VersionAccess Access { get; init; }

    /// <summary>Lifecycle state.</summary>
    public required VersionState State { get; init; }

    /// <summary>
    /// DEFAULT generation the branch last reconciled against (its merge base). Reconcile pulls
    /// DEFAULT changes since this cursor into the version.
    /// </summary>
    public required long CommonAncestorGeneration { get; init; }

    /// <summary>Latest generation produced by edits within this version.</summary>
    public required long BranchGeneration { get; init; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Timestamp (UTC) when the version was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp (UTC) when the version was last modified.</summary>
    public required DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>
/// The version a read or edit operation targets, threaded through the shared query/edit pipeline. A
/// null <see cref="VersionContext"/> (or <see cref="IsDefault"/>) means the DEFAULT version, which is
/// byte-identical to the non-versioned base path. Reads overlay the version's deltas onto the
/// DEFAULT base; writes set the transaction-scoped version GUC so the change-tracking trigger tags
/// produced changes with the version (#1272 Track B).
/// </summary>
public readonly record struct VersionContext
{
    /// <summary>
    /// The DEFAULT version context. Read/write paths must treat this exactly as the non-versioned
    /// base path (no overlay, no GUC).
    /// </summary>
    public static VersionContext Default => new() { VersionId = null };

    /// <summary>Target version id; null for DEFAULT.</summary>
    public Guid? VersionId { get; init; }

    /// <summary>Esri-style <c>owner.name</c> identity, for diagnostics/links; null for DEFAULT.</summary>
    public string? VersionName { get; init; }

    /// <summary>
    /// The branch's merge base (DEFAULT generation last reconciled against). Used by the read overlay
    /// to bound the version-delta scan; meaningless for DEFAULT.
    /// </summary>
    public long CommonAncestorGeneration { get; init; }

    /// <summary>True when this context targets the DEFAULT version.</summary>
    public bool IsDefault => VersionId is null;

    /// <summary>Creates a version context for a specific branch version.</summary>
    /// <param name="version">The resolved branch version.</param>
    /// <returns>A version context targeting <paramref name="version"/>.</returns>
    public static VersionContext ForVersion(GdbVersion version) => new()
    {
        VersionId = version.VersionId,
        VersionName = version.VersionName,
        CommonAncestorGeneration = version.CommonAncestorGeneration,
    };
}
