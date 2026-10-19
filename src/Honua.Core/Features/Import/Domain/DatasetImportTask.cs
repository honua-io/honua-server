// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

/// <summary>Destination in the actual deployment's operational database, never a connection selector.</summary>
/// <param name="Schema">Explicit canonical operational schema.</param>
/// <param name="Table">Explicit canonical destination table.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportDestination(string Schema, string Table);

/// <summary>Immutable provenance of an authorized local process output.</summary>
/// <param name="ProjectId">Original project.</param>
/// <param name="SnapshotId">Original retained snapshot.</param>
/// <param name="SnapshotDigest">Original snapshot digest.</param>
/// <param name="RunId">Actual successful run.</param>
/// <param name="Path">Canonical relative output path, never an arbitrary URI.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportArtifactSource(string ProjectId, string SnapshotId, string SnapshotDigest, string RunId, string Path);

/// <summary>Bounded source declaration; actual transferred bytes are verified separately.</summary>
/// <param name="Kind">browser-file or local-process-artifact.</param>
/// <param name="FileName">Bounded filename used for format detection.</param>
/// <param name="TotalBytes">Declared immutable source length.</param>
/// <param name="ExpectedSha256">Expected whole-content digest; required before resumable admission.</param>
/// <param name="Artifact">Required canonical provenance for a local process artifact.</param>
/// <param name="BrowserSelectionId">Original human-selected file identity retained on the bound turn.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportSource(string Kind, string FileName, long TotalBytes,
    string? ExpectedSha256 = null, DatasetImportArtifactSource? Artifact = null, string? BrowserSelectionId = null);

/// <summary>Explicit import behavior frozen into a task revision, with no credential or URL fields.</summary>
/// <param name="LoadMode">Canonical import load behavior.</param>
/// <param name="TargetSrid">Target coordinate reference identifier.</param>
/// <param name="SourceSrid">Optional declared source coordinate reference identifier.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatasetImportTaskOptions(ImportLoadMode LoadMode = ImportLoadMode.Append,
    int TargetSrid = 4326, int? SourceSrid = null);

/// <summary>Server-owned immutable dataset-import task version; status and byte receipts are separate.</summary>
/// <param name="TaskId">Server-minted identity.</param>
/// <param name="Revision">Positive immutable version.</param>
/// <param name="ContentHash">Digest of this exact typed ingest intent.</param>
/// <param name="OwnerActorId">Actual creating actor, never a client claim.</param>
/// <param name="TenantId">Actual captured tenant.</param>
/// <param name="TargetId">Actual deployment target.</param>
/// <param name="WorkspaceId">Workspace with current explicit membership.</param>
/// <param name="Destination">Frozen operational database destination.</param>
/// <param name="Source">Frozen source declaration.</param>
/// <param name="Options">Frozen bounded import options.</param>
/// <param name="UploadAdmissionDeadline">Server-persisted original upload admission deadline, never refreshed by replay.</param>
public sealed record DatasetImportTask(string TaskId, long Revision, string ContentHash,
    string OwnerActorId, string? TenantId, string TargetId, string WorkspaceId,
    DatasetImportDestination Destination, DatasetImportSource Source, DatasetImportTaskOptions Options,
    DateTimeOffset UploadAdmissionDeadline);
