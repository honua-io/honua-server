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
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Request to import one or more coverages from a legacy OGC Web Coverage
/// Service endpoint (WCS 1.x or 2.x). Slice 3 of issue #1030.
/// </summary>
public sealed record OgcWcsImportRequest
{
    /// <summary>
    /// Source WCS service URL — the capabilities endpoint root. Per-coverage
    /// <c>GetCoverage</c> requests are built relative to this URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// WCS protocol version to request. Defaults to <c>2.0.1</c> when not specified.
    /// Accepted values include <c>1.0.0</c>, <c>1.1.1</c>, <c>2.0.1</c>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Inventory artifact produced by the slice-1 OGC coverage scanner. Required.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Requested output format. The deterministic happy-path is
    /// <c>image/tiff</c>; any other value is classified as <c>manual-review</c>
    /// because the slice-2 ingestion pipeline only consumes GeoTIFF/COG bytes.
    /// Defaults to <c>image/tiff</c>.
    /// </summary>
    public string OutputFormat { get; init; } = "image/tiff";

    /// <summary>
    /// Coverage selection. When null or empty every coverage in the inventory is processed.
    /// Entries should match either <see cref="MigrationInventoryResource.Id"/> or
    /// <see cref="MigrationInventoryResource.Name"/>.
    /// </summary>
    public string[] CoverageSelection { get; init; } = [];

    /// <summary>
    /// Optional per-coverage import settings keyed by source coverage name or resource id.
    /// </summary>
    public IReadOnlyDictionary<string, OgcCoverageImportTarget> Targets { get; init; }
        = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal);

    /// <summary>
    /// When true the service performs the actual GeoTIFF download and registers
    /// the raster through the slice-2 coverage import pipeline. When false only a
    /// deterministic manifest is produced.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// When true callers explicitly request a dry-run preview manifest only.
    /// This is the safe default — <see cref="ApplyMode"/> must be true to mutate state.
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// Optional target service name used when computing manifest target identities.
    /// </summary>
    public string? TargetServiceName { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth username for protected WCS endpoints.
    /// Never echoed back in manifest output.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth password for protected WCS endpoints.
    /// Never echoed back in manifest output.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Whether to allow plain HTTP or local URLs (operator-controlled environments).
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}
