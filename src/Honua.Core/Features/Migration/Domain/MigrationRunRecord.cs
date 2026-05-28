// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Slice 5 of issue #1015. Operator-visible record of a single GeoServer
/// migration apply run. Persisted in <c>honua.migration_runs</c> by the
/// <see cref="Honua.Core.Features.Migration.Abstractions.IMigrationRunCatalog"/>
/// implementation so the admin endpoints — and the UI/SDK that drive them —
/// can list, inspect, cancel, and download evidence for past runs without
/// re-deriving state from per-stage artifacts.
/// </summary>
/// <remarks>
/// <para>
/// The record is intentionally narrow: it carries identity, status, timing,
/// source-identity hints, and a reference to the slice-4 evidence pack so
/// the admin endpoints can stream the pack back without re-running the
/// migration. Per-stage execution detail stays in
/// <see cref="MigrationApplyExecutionArtifact"/>.
/// </para>
/// <para>
/// Privacy posture: <see cref="SourceUrl"/> must already have userinfo,
/// query, and fragment stripped before persistence. The catalog writer is
/// the last line of defense, but callers are expected to redact upstream
/// the same way <see cref="MigrationEvidencePackArtifact"/> does.
/// </para>
/// </remarks>
public sealed record MigrationRunRecord
{
    /// <summary>
    /// Stable run identifier (UUID-shaped string). Acts as the primary key.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Redacted source URL (no userinfo, query, or fragment). May be empty
    /// when the run was driven by an offline manifest only.
    /// </summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display name for the source, when known.
    /// </summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>
    /// Current run status.
    /// </summary>
    public required MigrationRunStatus Status { get; init; }

    /// <summary>
    /// UTC instant the run started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// UTC instant the run reached a terminal status. Null while the run is
    /// still <see cref="MigrationRunStatus.Running"/>.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Reference to the slice-4 evidence pack for this run, when emitted.
    /// Implementations may use this as a storage key (filesystem path,
    /// object-store URI, or row id). Null until the pack is recorded.
    /// </summary>
    public string? EvidencePackRef { get; init; }

    /// <summary>
    /// Fingerprint of the slice-4 evidence pack bundle. Returned to API
    /// callers so they can prove they received the same pack later.
    /// </summary>
    public string? EvidencePackFingerprint { get; init; }

    /// <summary>
    /// Operator-visible note recorded on cancel or failure. Free-form, no
    /// secrets expected.
    /// </summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// Lifecycle status for a <see cref="MigrationRunRecord"/>.
/// </summary>
public enum MigrationRunStatus
{
    /// <summary>The run is still executing.</summary>
    Running,

    /// <summary>The run completed successfully.</summary>
    Succeeded,

    /// <summary>The run failed before reaching cutover.</summary>
    Failed,

    /// <summary>The run was cancelled by an operator.</summary>
    Cancelled
}
