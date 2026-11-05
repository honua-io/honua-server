// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VersionManagementServer.Models;

public sealed class VersionManagementServiceInfo
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    public string DefaultVersionName { get; init; } = "sde.DEFAULT";

    // Clients open the versioned workspace by reading this version and starting a read session on it before any named version is used.
    public required string DefaultVersionGuid { get; init; }

    public VersionManagementCapabilities Capabilities { get; init; } = new();
}

public sealed class VersionManagementCapabilities
{
    public bool SupportsConflictDetectionByAttribute { get; init; } = true;

    public bool SupportsPartialPost { get; init; }

    public bool SupportsDifferencesFromMoment { get; init; }

    public bool SupportsDifferencesWithLayers { get; init; }

    public bool SupportsAsyncReconcile { get; init; } = true;

    public bool SupportsAsyncPost { get; init; } = true;

    public bool SupportsAsyncDifferences { get; init; }

    public bool SupportsOutSR { get; init; }

    public bool SupportsPartialPostOperation { get; init; }

    public bool SupportsVersionInfosNameFilter { get; init; }

    public bool SupportsMultipleReadersSingleWriterLocking { get; init; }

    public bool SupportsLockInfos { get; init; }

    public bool SupportsCreateWithMoment { get; init; }
}

public sealed class VersionInfo
{
    public required string VersionGuid { get; init; }

    public required string VersionName { get; init; }

    public required string Owner { get; init; }

    public required string Access { get; init; }

    public required string Status { get; init; }

    public string? Description { get; init; }

    public string? ParentVersionGuid { get; init; }

    public long CreationMoment { get; init; }

    public long ModifiedMoment { get; init; }
}

public sealed class VersionListResponse
{
    public VersionInfo[] Versions { get; init; } = [];
}

// versionInfos uses creationDate and modifiedDate and omits the reconcile, evaluation, and ancestor dates because Honua tracks branch generations rather than those timestamps.
public sealed class VersionInfosEntry
{
    public required string VersionName { get; init; }

    public required string VersionGuid { get; init; }

    public string? Description { get; init; }

    public long CreationDate { get; init; }

    public long ModifiedDate { get; init; }

    public required string Access { get; init; }
}

public sealed class VersionInfosResponse
{
    public VersionInfosEntry[] Versions { get; init; } = [];

    public bool Success { get; init; } = true;
}

public sealed class CreateVersionResponse
{
    public bool Success { get; init; } = true;

    public required VersionInfo VersionInfo { get; init; }
}

public sealed class VersionMomentResponse
{
    public bool Success { get; init; } = true;

    public long Moment { get; init; }
}

public sealed class VersionConflictFieldDiffInfo
{
    public required string Name { get; init; }

    public string? Base { get; init; }

    public string? Default { get; init; }

    public string? Version { get; init; }
}

// Conflict images are opaque attribute JSON and geometry WKT and carry no SQL or provider internals.
public sealed class VersionConflictInfo
{
    public int LayerId { get; init; }

    public long ObjectId { get; init; }

    public required string ConflictType { get; init; }

    // Common-ancestor attribute image, or null when the ancestor is unknown.
    public string? BaseAttributes { get; init; }

    // DEFAULT attribute image, or null when that row was deleted.
    public string? DefaultAttributes { get; init; }

    // Branch attribute image, or null when that row was deleted.
    public string? VersionAttributes { get; init; }

    public string? BaseGeometry { get; init; }

    public string? DefaultGeometry { get; init; }

    public string? VersionGeometry { get; init; }

    public VersionConflictFieldDiffInfo[] FieldDiffs { get; init; } = [];
}

public sealed class ReconcileResponse
{
    public bool Success { get; init; } = true;

    public bool HasConflicts { get; init; }

    public bool CanPost { get; init; }

    public int AutoResolvedCount { get; init; }

    public bool Posted { get; init; }

    public int AppliedChanges { get; init; }

    public long ServerGeneration { get; init; }

    public VersionConflictInfo[] Conflicts { get; init; } = [];
}

public sealed class InspectConflictsResponse
{
    public bool Success { get; init; } = true;

    public bool HasConflicts { get; init; }

    public VersionConflictInfo[] Conflicts { get; init; } = [];
}

public sealed class ResolveConflictsResponse
{
    public bool Success { get; init; } = true;

    public int Resolved { get; init; }

    public int Remaining { get; init; }

    public bool CanPost { get; init; }
}

public sealed class PostResponse
{
    public bool Success { get; init; }

    public int AppliedChanges { get; init; }

    public long ServerGeneration { get; init; }

    public bool BlockedByConflicts { get; init; }
}

public sealed class VersionJobResponse
{
    public bool Success { get; init; } = true;

    public required string JobId { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public required string StatusUrl { get; init; }

    public int ConflictCount { get; init; }

    public int AutoResolvedCount { get; init; }

    public bool CanPost { get; init; }

    public int AppliedChanges { get; init; }

    public long ServerGeneration { get; init; }

    public bool BlockedByConflicts { get; init; }

    public string? Error { get; init; }
}
