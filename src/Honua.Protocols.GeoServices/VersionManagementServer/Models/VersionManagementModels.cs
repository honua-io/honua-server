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
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent, Esri-compatible server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoArcGisServerVersionTests).

    /// <summary>Standard extension display name.</summary>
    public string Name { get; init; } = "Version Management Server";

    /// <summary>The standard service-extension resource type.</summary>
    public string Type { get; init; } = "Map Server Extension";

    /// <summary>Canonical name of the managed base store.</summary>
    public string DefaultVersionName { get; init; } = "sde.DEFAULT";

    /// <summary>Persisted identity of the managed base store, never a fabricated branch GUID.</summary>
    public required string DefaultVersionGuid { get; init; }

    /// <summary>Truthful supported optional operations, in the standard object wire shape.</summary>
    public VersionManagementCapabilities Capabilities { get; init; } = new();
}

/// <summary>Optional VMS operations supported by the canonical implementation.</summary>
public sealed class VersionManagementCapabilities
{
    /// <summary>Reconcile supports attribute-level conflict detection.</summary>
    public bool SupportsConflictDetectionByAttribute { get; init; } = true;

    /// <summary>Reconcile can use the configured canonical job runner.</summary>
    public bool SupportsAsyncReconcile { get; init; }

    /// <summary>Post can use the configured canonical job runner.</summary>
    public bool SupportsAsyncPost { get; init; }

    /// <summary>Posting selected rows is not implemented.</summary>
    public bool SupportsPartialPost => false;

    /// <summary>Moment-filtered differences are not implemented.</summary>
    public bool SupportsDifferencesFromMoment => false;

    /// <summary>Layer-filtered differences are not implemented.</summary>
    public bool SupportsDifferencesWithLayers => false;

    /// <summary>Asynchronous differences are not implemented.</summary>
    public bool SupportsAsyncDifferences => false;

    /// <summary>Output projection of differences/conflicts is not implemented.</summary>
    public bool SupportsOutSR => false;

    /// <summary>The separate partial-post operation is not implemented.</summary>
    public bool SupportsPartialPostOperation => false;

    /// <summary>The versionInfos name-filter operation is not implemented.</summary>
    public bool SupportsVersionInfosNameFilter => false;

    /// <summary>Server-held Esri read/write session locks are not implemented.</summary>
    public bool SupportsMultipleReadersSingleWriterLocking => false;

    /// <summary>The Esri locks and lockInfos resources are not implemented.</summary>
    public bool SupportsLockInfos => false;

    /// <summary>Creating a version at a historical moment is not implemented.</summary>
    public bool SupportsCreateWithMoment => false;
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

    /// <summary>Standard version metadata creation date, matching the durable descriptor.</summary>
    public long CreationDate => CreationMoment;

    /// <summary>Standard metadata modification date; not a feature-data modification timestamp.</summary>
    public long ModifiedDate => ModifiedMoment;

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
    /// <summary>The durable DEFAULT descriptor and visible branch versions.</summary>
    public VersionInfo[] Versions { get; init; } = [];
}

/// <summary>Read-only versionInfos result after canonical access and owner filtering.</summary>
public sealed class VersionInfosResponse
{
    /// <summary>Visible identities, including DEFAULT when its display owner matches the filter.</summary>
    public VersionInfo[] Versions { get; init; } = [];

    /// <summary>True only when identity discovery completed successfully.</summary>
    public bool Success => true;
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
/// A single field-level three-way diff for a conflicting feature, surfaced so a client/UI can show the
/// base/DEFAULT/version values side by side for one attribute (#371).
/// </summary>
public sealed class VersionConflictFieldDiffInfo
{
    /// <summary>Attribute name.</summary>
    public required string Name { get; init; }

    /// <summary>Common-ancestor (base) value rendered as JSON text, or null when absent.</summary>
    public string? Base { get; init; }

    /// <summary>DEFAULT (target) value rendered as JSON text, or null when absent.</summary>
    public string? Default { get; init; }

    /// <summary>Branch (version) value rendered as JSON text, or null when absent.</summary>
    public string? Version { get; init; }
}

/// <summary>
/// Esri-shaped conflict descriptor returned by <c>reconcile</c>/<c>inspectConflicts</c> when version
/// edits collide with DEFAULT edits made since the merge base. Carries the three-way base/DEFAULT/
/// version feature images plus per-field diffs so a conflict-resolution UI can render a before/after/
/// base view (#371). Image fields are opaque JSON (attributes) and WKT (geometry) and never carry SQL,
/// connection details, or provider internals.
/// </summary>
public sealed class VersionConflictInfo
{
    /// <summary>Storage-layer id of the conflicting feature.</summary>
    public int LayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public long ObjectId { get; init; }

    /// <summary>Conflict classification (attribute, geometry, deleteUpdate, updateDelete, ...).</summary>
    public required string ConflictType { get; init; }

    /// <summary>Common-ancestor (base) attribute image as JSON text, or null when unknown.</summary>
    public string? BaseAttributes { get; init; }

    /// <summary>DEFAULT (target) attribute image as JSON text, or null when the row was deleted.</summary>
    public string? DefaultAttributes { get; init; }

    /// <summary>Branch (version) attribute image as JSON text, or null when the row was deleted.</summary>
    public string? VersionAttributes { get; init; }

    /// <summary>Common-ancestor (base) geometry as WKT, or null when absent.</summary>
    public string? BaseGeometry { get; init; }

    /// <summary>DEFAULT (target) geometry as WKT, or null when absent/deleted.</summary>
    public string? DefaultGeometry { get; init; }

    /// <summary>Branch (version) geometry as WKT, or null when absent/deleted.</summary>
    public string? VersionGeometry { get; init; }

    /// <summary>Per-field three-way diffs for the overlapping fields that made this a conflict.</summary>
    public VersionConflictFieldDiffInfo[] FieldDiffs { get; init; } = [];
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

    /// <summary>True when the version reconciled cleanly (or was auto-resolved) and may be posted.</summary>
    public bool CanPost { get; init; }

    /// <summary>Number of conflicts the supplied auto-resolution policy resolved.</summary>
    public int AutoResolvedCount { get; init; }

    /// <summary>
    /// True when <c>withPost=true</c> was requested and the clean reconcile posted the version in the
    /// same operation (#2135). False when not requested or when conflicts remained (no-op-with-conflicts).
    /// </summary>
    public bool Posted { get; init; }

    /// <summary>Net feature changes replayed onto DEFAULT when <see cref="Posted"/> is true; 0 otherwise.</summary>
    public int AppliedChanges { get; init; }

    /// <summary>DEFAULT generation produced by the <c>withPost</c> post when <see cref="Posted"/> is true; 0 otherwise.</summary>
    public long ServerGeneration { get; init; }

    /// <summary>Unresolved conflicts; non-empty blocks post.</summary>
    public VersionConflictInfo[] Conflicts { get; init; } = [];
}

/// <summary>
/// Response for the <c>inspectConflicts</c> operation: the current pending conflict set for a version
/// with the three-way images for a manual-resolution UI (#371).
/// </summary>
public sealed class InspectConflictsResponse
{
    /// <summary>Always true on success.</summary>
    public bool Success { get; init; } = true;

    /// <summary>True when there are pending conflicts blocking post.</summary>
    public bool HasConflicts { get; init; }

    /// <summary>The pending conflicts.</summary>
    public VersionConflictInfo[] Conflicts { get; init; } = [];
}

/// <summary>
/// Response for the <c>resolveConflicts</c> operation: the outcome of applying manual resolutions (#371).
/// </summary>
public sealed class ResolveConflictsResponse
{
    /// <summary>Always true on success.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Number of conflicts transitioned to resolved by this call.</summary>
    public int Resolved { get; init; }

    /// <summary>Number of pending conflicts still blocking post after this call.</summary>
    public int Remaining { get; init; }

    /// <summary>True when no pending conflicts remain and the version may be posted.</summary>
    public bool CanPost { get; init; }
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

/// <summary>
/// Response for an asynchronous <c>reconcile</c>/<c>post</c> job (#1553). Returned with HTTP 202 when a
/// caller requests async execution, and from the job-status poll endpoint. Carries the job handle and,
/// once the job is terminal, the same outcome fields the synchronous reconcile/post responses report.
/// </summary>
public sealed class VersionJobResponse
{
    /// <summary>Always true: the job was accepted/queried successfully (job outcome is in <see cref="Status"/>).</summary>
    public bool Success { get; init; } = true;

    /// <summary>Stable job identifier; poll the job-status endpoint with this id.</summary>
    public required string JobId { get; init; }

    /// <summary>Whether the job reconciles or posts: <c>reconcile</c> or <c>post</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Lifecycle status: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c>, or
    /// <c>lockContended</c> (another reconcile/post for the version is in progress).
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Relative URL to poll for this job's status.</summary>
    public required string StatusUrl { get; init; }

    /// <summary>Unresolved conflicts after a reconcile (0 until a reconcile job completes).</summary>
    public int ConflictCount { get; init; }

    /// <summary>Conflicts a reconcile policy auto-resolved.</summary>
    public int AutoResolvedCount { get; init; }

    /// <summary>True when a reconcile left the version clear to post.</summary>
    public bool CanPost { get; init; }

    /// <summary>Net feature changes a post replayed onto DEFAULT.</summary>
    public int AppliedChanges { get; init; }

    /// <summary>DEFAULT generation produced by a post (or reconciled-to generation).</summary>
    public long ServerGeneration { get; init; }

    /// <summary>True when a post was refused because unresolved conflicts remain.</summary>
    public bool BlockedByConflicts { get; init; }

    /// <summary>Sanitized error message when the job failed; null otherwise.</summary>
    public string? Error { get; init; }
}

