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

    /// <summary>Default version name (always the implicit DEFAULT version).</summary>
    public string DefaultVersionName { get; init; } = "sde.DEFAULT";

    /// <summary>
    /// GUID of the implicit DEFAULT version. Clients open the versioned workspace by reading this
    /// version's resource and starting a read session on it before any named version is used.
    /// </summary>
    public required string DefaultVersionGuid { get; init; }

    /// <summary>Named capability flags advertised for the version-management surface.</summary>
    public VersionManagementCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// Version-management discovery flags. Advertises implemented canonical operations and keeps
/// unsupported optional operations disabled in the Esri capability-object wire format.
/// </summary>
public sealed class VersionManagementCapabilities
{
    /// <summary>Whether reconcile can detect conflicts by attribute.</summary>
    public bool SupportsConflictDetectionByAttribute { get; init; } = true;

    /// <summary>Whether post accepts a subset of changed rows.</summary>
    public bool SupportsPartialPost { get; init; }

    /// <summary>Whether differences can be requested from a specified moment.</summary>
    public bool SupportsDifferencesFromMoment { get; init; }

    /// <summary>Whether differences can be filtered by layer.</summary>
    public bool SupportsDifferencesWithLayers { get; init; }

    /// <summary>Whether reconcile supports asynchronous execution through the shared job runner.</summary>
    public bool SupportsAsyncReconcile { get; init; } = true;

    /// <summary>Whether post supports asynchronous execution through the shared job runner.</summary>
    public bool SupportsAsyncPost { get; init; } = true;

    /// <summary>Whether differences supports asynchronous execution.</summary>
    public bool SupportsAsyncDifferences { get; init; }

    /// <summary>Whether differences and conflicts support an output spatial reference.</summary>
    public bool SupportsOutSR { get; init; }

    /// <summary>Whether the partialPost operation is implemented.</summary>
    public bool SupportsPartialPostOperation { get; init; }

    /// <summary>Whether versionInfos can filter by version name.</summary>
    public bool SupportsVersionInfosNameFilter { get; init; }

    /// <summary>Whether version locks support concurrent readers and a single writer.</summary>
    public bool SupportsMultipleReadersSingleWriterLocking { get; init; }

    /// <summary>Whether the locks resource and lockInfos operation are implemented.</summary>
    public bool SupportsLockInfos { get; init; }

    /// <summary>Whether create supports branching from a specified historical moment.</summary>
    public bool SupportsCreateWithMoment { get; init; }
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
/// One version entry of the GeoServices <c>versionInfos</c> operation. Unlike
/// <see cref="VersionInfo"/>, it uses the contract's field names (<c>creationDate</c>,
/// <c>modifiedDate</c>); the reconcile, evaluation and ancestor dates are omitted because Honua
/// tracks branch generations, not those timestamps.
/// </summary>
public sealed class VersionInfosEntry
{
    /// <summary>Esri-style <c>owner.name</c> version identity.</summary>
    public required string VersionName { get; init; }

    /// <summary>Stable version identifier (registry-format GUID).</summary>
    public required string VersionGuid { get; init; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Epoch-milliseconds the version was created.</summary>
    public long CreationDate { get; init; }

    /// <summary>Epoch-milliseconds the version was last modified.</summary>
    public long ModifiedDate { get; init; }

    /// <summary>Access level: <c>private</c>, <c>protected</c>, or <c>public</c>.</summary>
    public required string Access { get; init; }
}

/// <summary>
/// Response for the GeoServices <c>versionInfos</c> operation: DEFAULT first, then the versions
/// visible to the caller, with the contract's <c>success</c> flag.
/// </summary>
public sealed class VersionInfosResponse
{
    /// <summary>DEFAULT followed by the visible branch versions.</summary>
    public VersionInfosEntry[] Versions { get; init; } = [];

    /// <summary>Always true for a successful listing; failures answer with an error document.</summary>
    public bool Success { get; init; } = true;
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
