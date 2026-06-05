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
/// Classification of an unresolved reconcile conflict between version edits and DEFAULT edits made
/// since the version's merge base. Reuses the disconnected-sync conflict taxonomy (#1272 Track B).
/// </summary>
/// <param name="LayerId">Storage-layer id of the conflicting feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
/// <param name="ConflictType">Conflict classification.</param>
public readonly record struct VersionReconcileConflict(
    int LayerId,
    long ObjectId,
    ReplicaConflictType ConflictType);

/// <summary>
/// Result of a reconcile: the conflicts detected between the version and DEFAULT since the merge
/// base, and whether the version is clear to post. A reconcile pulls DEFAULT changes since
/// <see cref="GdbVersion.CommonAncestorGeneration"/> into the version, applies auto-resolution
/// policies (owned by #371), and reports any remaining conflicts that block <c>Post</c>.
/// </summary>
/// <param name="Conflicts">Unresolved conflicts; non-empty blocks post.</param>
/// <param name="CanPost">True when the version reconciled cleanly and may be posted.</param>
/// <param name="NewCommonAncestorGeneration">The DEFAULT generation the version is now reconciled to.</param>
public readonly record struct VersionReconcileResult(
    ImmutableArray<VersionReconcileConflict> Conflicts,
    bool CanPost,
    long NewCommonAncestorGeneration);

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
