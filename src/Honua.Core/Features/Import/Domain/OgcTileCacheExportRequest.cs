// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to export a deterministic XYZ/TMS tile cache from a slice-3 WMTS
/// migration plan entry classified as automated. The export fetches the
/// configured zoom-level window from the source WMTS endpoint and persists
/// the bytes through an <see cref="Abstractions.IOgcTileCacheSink"/>.
/// </summary>
public sealed record OgcTileCacheExportRequest
{
    /// <summary>
    /// Optional job identifier used for log correlation.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// WMTS service URL. May be a service root or an existing
    /// <c>GetCapabilities</c> URL. Used by the inventory scanner when no
    /// pre-built manifest is supplied.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Optional WMTS version. When omitted, the version advertised by the
    /// source is used.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional list of source WMTS layer identifiers to export. When omitted,
    /// every automated tile-set plan entry is exported.
    /// </summary>
    public string[]? LayerIdentifiers { get; init; }

    /// <summary>
    /// Optional tile-matrix-set identifiers to restrict the export to. When
    /// omitted, every automated tile-matrix-set plan entry is exported.
    /// </summary>
    public string[]? TileMatrixSetIdentifiers { get; init; }

    /// <summary>
    /// Output tile format the source should be asked for, such as
    /// <c>image/png</c>. Defaults to <c>image/png</c>.
    /// </summary>
    public string? TileFormat { get; init; }

    /// <summary>
    /// Optional WMTS style identifier to fetch tiles with. Defaults to
    /// <c>default</c>.
    /// </summary>
    public string? StyleIdentifier { get; init; }

    /// <summary>
    /// Minimum zoom level (inclusive). Defaults to 0.
    /// </summary>
    public int MinZoom { get; init; } = OgcTileCacheExportSafetyLimits.DefaultMinZoom;

    /// <summary>
    /// Maximum zoom level (inclusive). Defaults to
    /// <see cref="OgcTileCacheExportSafetyLimits.DefaultMaxZoom"/>.
    /// </summary>
    public int MaxZoom { get; init; } = OgcTileCacheExportSafetyLimits.DefaultMaxZoom;

    /// <summary>
    /// Optional column window (inclusive) per zoom level. When omitted, the
    /// full grid for the zoom level is exported up to the tile-count safety cap.
    /// </summary>
    public OgcTileCacheTileWindow? Window { get; init; }

    /// <summary>
    /// Whether to perform a dry run that resolves the plan and tile URLs but
    /// does not fetch tile bytes or write to the sink.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Explicit operator opt-in for catalog mutation. When <see cref="DryRun"/>
    /// is <c>false</c>, the import endpoint rejects the request unless
    /// <c>ApplyMode</c> is <c>true</c>.
    /// </summary>
    public bool ApplyMode { get; init; }

    /// <summary>
    /// Per-tile HTTP timeout in seconds. Defaults to 30 seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether HTTP and local URLs are allowed for test or controlled operator
    /// environments.
    /// </summary>
    public bool AllowUnsafeLocalUrls { get; init; }
}

/// <summary>
/// Inclusive (z, x, y) tile window. The window applies uniformly per zoom; if
/// the requested column or row exceeds the grid extent for a zoom, the export
/// clamps to the valid range.
/// </summary>
public sealed record OgcTileCacheTileWindow
{
    /// <summary>Inclusive minimum tile column.</summary>
    public required int MinColumn { get; init; }

    /// <summary>Inclusive maximum tile column.</summary>
    public required int MaxColumn { get; init; }

    /// <summary>Inclusive minimum tile row.</summary>
    public required int MinRow { get; init; }

    /// <summary>Inclusive maximum tile row.</summary>
    public required int MaxRow { get; init; }
}

/// <summary>
/// Safety thresholds applied by <see cref="Abstractions.IOgcTileCacheExportService"/>.
/// </summary>
public static class OgcTileCacheExportSafetyLimits
{
    /// <summary>Default minimum zoom level for slice-4 automated tile exports.</summary>
    public const int DefaultMinZoom = 0;

    /// <summary>
    /// Default maximum zoom level for slice-4 automated tile exports. Chosen
    /// to keep export fixtures bounded. Operators may opt in to a larger
    /// window per request, subject to <see cref="MaxAutomatedZoomLevel"/>.
    /// </summary>
    public const int DefaultMaxZoom = 2;

    /// <summary>
    /// Hard upper bound on the per-request maximum zoom level. Beyond this
    /// the tile-set is reclassified as <c>manual-review</c> because automated
    /// export would request too many tiles for a single migration run.
    /// </summary>
    public const int MaxAutomatedZoomLevel = 6;

    /// <summary>
    /// Hard upper bound on the per-request total tile count. Beyond this the
    /// tile-set is reclassified as <c>manual-review</c>.
    /// </summary>
    public const int MaxAutomatedTileCount = 1024;
}

/// <summary>
/// Result of a tile-cache export operation.
/// </summary>
public sealed record OgcTileCacheExportResult
{
    /// <summary>Whether the export completed without a top-level error.</summary>
    public required bool Success { get; init; }

    /// <summary>Whether this was a dry run (no tile bytes were written).</summary>
    public bool WasDryRun { get; init; }

    /// <summary>Source WMTS URL the export ran against.</summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>Number of tile-set plan entries considered for export.</summary>
    public int TileSetsPlanned { get; init; }

    /// <summary>Number of tile-set plan entries exported (or that would have exported in a dry run).</summary>
    public int TileSetsExported { get; init; }

    /// <summary>Number of tile-set plan entries skipped (manual-review, threshold exceeded, or error).</summary>
    public int TileSetsSkipped { get; init; }

    /// <summary>Total number of tile records persisted across all tile-sets.</summary>
    public int TilesPersisted { get; init; }

    /// <summary>Total number of tile records skipped because they already existed in the sink.</summary>
    public int TilesAlreadyPresent { get; init; }

    /// <summary>Total number of tile records that failed to fetch or persist.</summary>
    public int TilesFailed { get; init; }

    /// <summary>Per-tile-set outcome records.</summary>
    public IReadOnlyList<OgcTileCacheExportedTileSet> TileSets { get; init; } = [];

    /// <summary>Top-level warnings encountered during the export.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Top-level error message when <see cref="Success"/> is <c>false</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Migration manifest the export was derived from.</summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>Export duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Per-tile-set record describing one tile-cache export outcome.
/// </summary>
public sealed record OgcTileCacheExportedTileSet
{
    /// <summary>Source WMTS layer identifier.</summary>
    public required string LayerIdentifier { get; init; }

    /// <summary>Source WMTS tile-matrix-set identifier.</summary>
    public required string TileMatrixSetIdentifier { get; init; }

    /// <summary>Stable target tile-cache identifier persisted into the catalog.</summary>
    public string? TargetTileCacheId { get; init; }

    /// <summary>Resolved minimum zoom level for the export.</summary>
    public int MinZoom { get; init; }

    /// <summary>Resolved maximum zoom level for the export.</summary>
    public int MaxZoom { get; init; }

    /// <summary>Number of tiles persisted by this export.</summary>
    public int TilesPersisted { get; init; }

    /// <summary>Number of tiles that already existed in the sink and were left untouched.</summary>
    public int TilesAlreadyPresent { get; init; }

    /// <summary>Number of tiles that failed to fetch or persist.</summary>
    public int TilesFailed { get; init; }

    /// <summary>Fidelity classification, one of <see cref="MigrationFidelityAutomationStatuses"/>.</summary>
    public required string Classification { get; init; }

    /// <summary>Stable machine-readable code summarising the export outcome.</summary>
    public required string Code { get; init; }

    /// <summary>Per-tile-set warnings.</summary>
    public string[] Warnings { get; init; } = [];
}
