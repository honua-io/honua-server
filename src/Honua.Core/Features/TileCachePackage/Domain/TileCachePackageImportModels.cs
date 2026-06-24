// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Domain;

/// <summary>
/// Request to import an Esri tile-cache package into Honua's tile catalog (#1269).
/// </summary>
public sealed record TileCachePackageImportRequest
{
    /// <summary>Seekable stream over the uploaded package archive.</summary>
    public required Stream Package { get; init; }

    /// <summary>Original uploaded file name (used for format detection and provenance).</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Logical tileset identifier the imported tiles bind to. Becomes the
    /// catalog layer identifier and the address segment served tiles are fetched
    /// under. Required.
    /// </summary>
    public required string TilesetId { get; init; }

    /// <summary>Optional inclusive minimum zoom to import (defaults to the package minimum).</summary>
    public int? MinZoom { get; init; }

    /// <summary>Optional inclusive maximum zoom to import (defaults to the package maximum).</summary>
    public int? MaxZoom { get; init; }

    /// <summary>
    /// Preview mode: parse the descriptor and report the plan without writing any
    /// tiles into the catalog.
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Outcome of a tile-cache package import.
/// </summary>
public sealed record TileCachePackageImportResult
{
    /// <summary>Whether the import (or dry-run plan) succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Stable tile-cache identifier the tiles were written to / would be served from.</summary>
    public string? TileCacheId { get; init; }

    /// <summary>Detected storage format.</summary>
    public required string StorageFormat { get; init; }

    /// <summary>Detected payload kind: <c>raster</c> or <c>vector</c>.</summary>
    public required string DataType { get; init; }

    /// <summary>Served tile content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Resolved Honua tile-matrix-set identifier.</summary>
    public required string TileMatrixSet { get; init; }

    /// <summary>Resolved minimum zoom imported.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Resolved maximum zoom imported.</summary>
    public required int MaxZoom { get; init; }

    /// <summary>Number of tiles inserted (zero for dry-run or fully-deduplicated re-imports).</summary>
    public required int TilesImported { get; init; }

    /// <summary>Number of tiles skipped because they already existed in the catalog.</summary>
    public required int TilesSkipped { get; init; }

    /// <summary>Whether this was a dry-run (no tiles written).</summary>
    public required bool DryRun { get; init; }
}
