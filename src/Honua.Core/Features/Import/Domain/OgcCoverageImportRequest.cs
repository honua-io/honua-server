// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to import one or more coverages from an OGC WCS or OGC API Coverages source
/// using the slice-1 migration inventory as the source-of-truth selection.
/// </summary>
public sealed record OgcCoverageImportRequest
{
    /// <summary>
    /// Coverage protocol surface. Supported values are <c>WCS</c> and <c>OGC API Coverages</c>.
    /// </summary>
    public required string ServiceType { get; init; }

    /// <summary>
    /// Source service URL (capability or landing-page root).
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional service version to request.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Inventory artifact produced by the slice-1 scanner. Required.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

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
    /// When true the service performs the actual GeoTIFF/COG download and
    /// registers the raster through <c>IRasterImportService</c>. When false
    /// only a deterministic manifest is produced.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// When true callers explicitly request a dry-run preview manifest only.
    /// This is the safe default — applyMode must be true to mutate state.
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// Optional target service name used when computing manifest target identities.
    /// </summary>
    public string? TargetServiceName { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth username for protected coverage endpoints.
    /// Never echoed back in manifest output.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional HTTP basic-auth password for protected coverage endpoints.
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

/// <summary>
/// Per-coverage import target options.
/// </summary>
public sealed record OgcCoverageImportTarget
{
    /// <summary>
    /// Destination raster layer identifier in Honua. Required when applyMode is true.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Optional target raster display name. Defaults to the source coverage name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional target raster description.
    /// </summary>
    public string? Description { get; init; }
}
