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
/// Result of a legacy OGC WCS coverage import attempt. Slice 3 of issue #1030.
/// </summary>
/// <remarks>
/// The WCS service delegates the actual catalog ingestion to the slice-2
/// coverage import service, so this result wraps the underlying
/// <see cref="OgcCoverageImportResult"/> and exposes the negotiated output
/// format the WCS layer requested on the caller's behalf.
/// </remarks>
public sealed record OgcWcsImportResult
{
    /// <summary>
    /// Deterministic migration manifest produced by the underlying coverage import.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Per-coverage execution records produced by the slice-2 ingestion pipeline.
    /// </summary>
    public OgcCoverageImportRecord[] Records { get; init; } = [];

    /// <summary>
    /// Output format the WCS layer requested from the source service
    /// (e.g. <c>image/tiff</c>). Mirrored back so callers can confirm
    /// the negotiation outcome without re-parsing per-record output formats.
    /// </summary>
    public required string RequestedOutputFormat { get; init; }

    /// <summary>
    /// WCS protocol version resolved for this request.
    /// </summary>
    public required string ResolvedVersion { get; init; }

    /// <summary>
    /// Whether the request ran in apply mode.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// Whether the request was a dry-run preview.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Coverage-style migration diagnostics surfaced by the underlying coverage
    /// import service. Slice 4 of issue #1030.
    /// </summary>
    public MigrationCoverageStyleDiagnostic[] StyleDiagnostics { get; init; } = [];
}
