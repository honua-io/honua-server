// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VersionManagementServer.Models;

/// <summary>
/// Service-info response for the GeoServices VersionManagementServer endpoint. Advertises the
/// branch-versioning capability surface so Esri clients (ArcGIS Pro, ArcGIS API for Python
/// <c>VersionManagementServer</c>) can discover the version lifecycle operations (#1272, ADR-0051).
/// </summary>
public sealed class VersionManagementServiceInfo
{
    /// <summary>Current GeoServices REST API version reported by the service.</summary>
    public double CurrentVersion { get; init; } = 10.91;

    /// <summary>Default version name (always the implicit DEFAULT version).</summary>
    public string DefaultVersionName { get; init; } = "sde.DEFAULT";

    /// <summary>Capability list advertised for the version-management surface.</summary>
    public string Capabilities { get; init; } = "Create,Delete,Alter,Reconcile,Post";
}

/// <summary>
/// Esri-shaped representation of a single branch version returned by the VersionManagementServer
/// <c>versions</c> / <c>versionInfo</c> operations.
/// </summary>
public sealed class VersionInfo
{
    /// <summary>Stable version identifier (GUID).</summary>
    public required string VersionGuid { get; init; }

    /// <summary>Esri-style <c>owner.name</c> version identity.</summary>
    public required string VersionName { get; init; }

    /// <summary>Version owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Access level: <c>private</c>, <c>protected</c>, or <c>public</c>.</summary>
    public required string Access { get; init; }

    /// <summary>Lifecycle state: <c>active</c>, <c>reconciling</c>, <c>posting</c>, or <c>deleted</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Parent version GUID this branch was created from; null when branched from DEFAULT.</summary>
    public string? ParentVersionGuid { get; init; }

    /// <summary>Epoch-milliseconds the version was created.</summary>
    public long CreationMoment { get; init; }

    /// <summary>Epoch-milliseconds the version was last modified.</summary>
    public long ModifiedMoment { get; init; }
}

/// <summary>
/// Response for the <c>versions</c> (list) operation.
/// </summary>
public sealed class VersionListResponse
{
    /// <summary>The known branch versions (DEFAULT is implicit and not listed).</summary>
    public VersionInfo[] Versions { get; init; } = [];
}

/// <summary>
/// Response for the <c>create</c> operation.
/// </summary>
public sealed class CreateVersionResponse
{
    /// <summary>Always true on success.</summary>
    public bool Success { get; init; } = true;

    /// <summary>The created version's identity.</summary>
    public required VersionInfo VersionInfo { get; init; }
}

/// <summary>
/// Generic success/moment response for delete/alter/start*/stop* operations.
/// </summary>
public sealed class VersionMomentResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Epoch-milliseconds at which the operation was acknowledged.</summary>
    public long Moment { get; init; }
}

/// <summary>
/// Esri-shaped conflict descriptor returned by <c>reconcile</c> when version edits collide with
/// DEFAULT edits made since the merge base.
/// </summary>
public sealed class VersionConflictInfo
{
    /// <summary>Storage-layer id of the conflicting feature.</summary>
    public int LayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public long ObjectId { get; init; }

    /// <summary>Conflict classification (attribute, geometry, deleteUpdate, updateDelete, ...).</summary>
    public required string ConflictType { get; init; }
}

/// <summary>
/// Response for the <c>reconcile</c> operation.
/// </summary>
public sealed class ReconcileResponse
{
    /// <summary>Whether the reconcile ran (always true; conflicts are reported separately).</summary>
    public bool Success { get; init; } = true;

    /// <summary>True when the reconcile detected unresolved conflicts that block <c>post</c>.</summary>
    public bool HasConflicts { get; init; }

    /// <summary>True when the version reconciled cleanly and may be posted.</summary>
    public bool CanPost { get; init; }

    /// <summary>Unresolved conflicts; non-empty blocks post.</summary>
    public VersionConflictInfo[] Conflicts { get; init; } = [];
}

/// <summary>
/// Response for the <c>post</c> operation.
/// </summary>
public sealed class PostResponse
{
    /// <summary>Whether the post committed.</summary>
    public bool Success { get; init; }

    /// <summary>Number of net feature changes replayed onto DEFAULT.</summary>
    public int AppliedChanges { get; init; }

    /// <summary>DEFAULT generation produced by the post.</summary>
    public long ServerGeneration { get; init; }

    /// <summary>True when the post was refused because unresolved conflicts remain.</summary>
    public bool BlockedByConflicts { get; init; }
}
