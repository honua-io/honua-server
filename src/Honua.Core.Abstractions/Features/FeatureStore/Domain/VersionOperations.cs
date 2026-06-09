// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Request to create a branch version off a parent (DEFAULT when <see cref="ParentVersion"/> is null).
/// </summary>
/// <param name="VersionName">Esri-style <c>owner.name</c> identity.</param>
/// <param name="Owner">Version owner.</param>
/// <param name="Access">Access level.</param>
/// <param name="ParentVersion">Parent version id; null branches from DEFAULT.</param>
/// <param name="Description">Optional description.</param>
public readonly record struct CreateVersionRequest(
    string VersionName,
    string Owner,
    VersionAccess Access,
    Guid? ParentVersion = null,
    string? Description = null);

/// <summary>
/// Request to alter a version's mutable metadata. Null fields are left unchanged.
/// </summary>
/// <param name="VersionId">Version to alter.</param>
/// <param name="VersionName">New name, or null to leave unchanged.</param>
/// <param name="Access">New access level, or null to leave unchanged.</param>
/// <param name="Description">New description, or null to leave unchanged.</param>
public readonly record struct AlterVersionRequest(
    Guid VersionId,
    string? VersionName = null,
    VersionAccess? Access = null,
    string? Description = null);

/// <summary>
/// Auto-resolution policy applied by the version manager's <c>ReconcileAsync</c> when a
/// reconcile detects overlapping edits between a branch version and DEFAULT since the merge base
/// (#371). When a deterministic policy is supplied the manager resolves each conflict in place so the
/// version can be posted; when <see cref="None"/> the manager preserves the manual-review behavior
/// (conflicts are reported, persisted, and block <c>Post</c> until resolved).
/// </summary>
public enum VersionReconcilePolicy
{
    /// <summary>
    /// No automatic resolution. Overlapping edits are reported as conflicts and block <c>Post</c>
    /// until an operator resolves them through the manual conflict-resolution surface. This is the
    /// default and matches the substrate's pre-#371 behavior.
    /// </summary>
    None = 0,

    /// <summary>
    /// Resolve each conflict in favor of the edit made most recently (by change generation). Maps to
    /// Esri's last-write-wins style auto-merge.
    /// </summary>
    LastWriteWins = 1,

    /// <summary>Resolve every conflict in favor of the branch (edit) version.</summary>
    VersionWins = 2,

    /// <summary>Resolve every conflict in favor of the target (DEFAULT) version.</summary>
    DefaultWins = 3,
}

/// <summary>
/// Operator choice for a single manually-resolved reconcile conflict (#371). Selects which side of a
/// three-way conflict (base / DEFAULT / version) becomes the posted state for the feature. Field-level
/// merges are produced automatically during reconcile for disjoint field edits, so the manual surface
/// resolves only genuinely-overlapping conflicts at the feature grain.
/// </summary>
public enum VersionConflictResolutionChoice
{
    /// <summary>Take the branch (edit) version's image for the conflicting feature.</summary>
    TakeVersion = 0,

    /// <summary>Take the target (DEFAULT) image for the conflicting feature.</summary>
    TakeDefault = 1,

    /// <summary>Take the common-ancestor (base) image for the conflicting feature.</summary>
    TakeBase = 2,
}

/// <summary>
/// A single field-level difference observed for a conflicting feature: the attribute name and its
/// base, DEFAULT, and version values rendered as opaque strings. Lets a client/UI present a precise
/// per-field three-way diff without coupling to the physical attribute schema.
/// </summary>
/// <param name="Name">Attribute name.</param>
/// <param name="Base">Common-ancestor value (string-rendered), or null when absent.</param>
/// <param name="Default">DEFAULT (target) value (string-rendered), or null when absent.</param>
/// <param name="Version">Branch (edit) value (string-rendered), or null when absent.</param>
public readonly record struct VersionConflictFieldDiff(
    string Name,
    string? Base,
    string? Default,
    string? Version);

/// <summary>
/// Classification of an unresolved reconcile conflict between version edits and DEFAULT edits made
/// since the version's merge base, with the three-way feature images that let a client/UI render a
/// before/after/base diff (#1272 Track B substrate; before/after/base + field diffs added by #371).
/// The image fields are opaque pre-serialized JSON (attribute image) and WKT (geometry) so the
/// conflict-review surface stays decoupled from the physical feature schema and never leaks SQL,
/// connection details, or provider internals.
/// </summary>
/// <param name="LayerId">Storage-layer id of the conflicting feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
/// <param name="ConflictType">Conflict classification.</param>
/// <param name="BaseAttributesJson">Common-ancestor attribute image (JSON), or null when unknown.</param>
/// <param name="DefaultAttributesJson">DEFAULT (target) attribute image (JSON), or null when the row was deleted.</param>
/// <param name="VersionAttributesJson">Branch (edit) attribute image (JSON), or null when the row was deleted.</param>
/// <param name="BaseGeometryWkt">Common-ancestor geometry (WKT), or null when absent.</param>
/// <param name="DefaultGeometryWkt">DEFAULT (target) geometry (WKT), or null when absent/deleted.</param>
/// <param name="VersionGeometryWkt">Branch (edit) geometry (WKT), or null when absent/deleted.</param>
/// <param name="FieldDiffs">Per-field three-way diffs for the overlapping fields that made this a conflict.</param>
public readonly record struct VersionReconcileConflict(
    int LayerId,
    long ObjectId,
    ReplicaConflictType ConflictType,
    string? BaseAttributesJson = null,
    string? DefaultAttributesJson = null,
    string? VersionAttributesJson = null,
    string? BaseGeometryWkt = null,
    string? DefaultGeometryWkt = null,
    string? VersionGeometryWkt = null,
    ImmutableArray<VersionConflictFieldDiff> FieldDiffs = default);

/// <summary>
/// Result of a reconcile: the conflicts detected between the version and DEFAULT since the merge
/// base, and whether the version is clear to post. A reconcile pulls DEFAULT changes since
/// <see cref="GdbVersion.CommonAncestorGeneration"/> into the version, auto-merges disjoint
/// field-level edits, applies the supplied <see cref="VersionReconcilePolicy"/> auto-resolution
/// policy (#371), and reports any remaining conflicts that block <c>Post</c>.
/// </summary>
/// <param name="Conflicts">Unresolved conflicts after auto-merge and policy application; non-empty blocks post.</param>
/// <param name="CanPost">True when the version reconciled cleanly (or was auto-resolved) and may be posted.</param>
/// <param name="NewCommonAncestorGeneration">The DEFAULT generation the version is now reconciled to.</param>
/// <param name="AutoResolvedCount">Number of conflicts the supplied policy resolved automatically.</param>
public readonly record struct VersionReconcileResult(
    ImmutableArray<VersionReconcileConflict> Conflicts,
    bool CanPost,
    long NewCommonAncestorGeneration,
    int AutoResolvedCount = 0);

/// <summary>
/// One operator-submitted resolution choice for a pending reconcile conflict (#371).
/// </summary>
/// <param name="LayerId">Storage-layer id of the conflicting feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
/// <param name="Choice">Which side of the three-way conflict to keep.</param>
public readonly record struct VersionConflictResolution(
    int LayerId,
    long ObjectId,
    VersionConflictResolutionChoice Choice);

/// <summary>
/// Outcome of applying a batch of manual conflict resolutions to a version (#371).
/// </summary>
/// <param name="Resolved">Number of conflicts transitioned to resolved by this call.</param>
/// <param name="Remaining">Number of pending conflicts still blocking <c>Post</c> after this call.</param>
/// <param name="CanPost">True when no pending conflicts remain and the version may be posted.</param>
public readonly record struct VersionConflictResolutionResult(
    int Resolved,
    int Remaining,
    bool CanPost);

/// <summary>
/// Result of a post: after a clean reconcile, the version's net changes are replayed onto DEFAULT in
/// a single transaction and the generation advances.
/// </summary>
/// <param name="Posted">True when the post committed.</param>
/// <param name="AppliedChanges">Number of net feature changes replayed onto DEFAULT.</param>
/// <param name="ServerGeneration">DEFAULT generation produced by the post.</param>
/// <param name="BlockedByConflicts">True when the post was refused because unresolved conflicts remain.</param>
public readonly record struct VersionPostResult(
    bool Posted,
    int AppliedChanges,
    long ServerGeneration,
    bool BlockedByConflicts);
