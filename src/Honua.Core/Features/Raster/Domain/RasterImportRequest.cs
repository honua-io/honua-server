// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Request to import a raster file into a layer.
/// </summary>
public sealed record RasterImportRequest
{
    /// <summary>
    /// Layer identifier to import the raster into.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Display name for the imported raster.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description for the raster dataset.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Path to the staged raster file on local disk.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Original filename (used for format detection).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Detected raster format.
    /// </summary>
    public required SupportedRasterFormat Format { get; init; }

    /// <summary>
    /// Explicit SRID override. If null, CRS is detected from file metadata.
    /// Required for world-file formats without a .prj sidecar.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// World file content for PNG/JPEG imports. Null for GeoTIFF.
    /// </summary>
    public string? WorldFileContent { get; init; }

    /// <summary>
    /// PRJ file content for PNG/JPEG imports. Null for GeoTIFF.
    /// </summary>
    public string? PrjFileContent { get; init; }

    /// <summary>
    /// Optional acquisition timestamp associated with the imported raster content.
    /// </summary>
    public DateTimeOffset? AcquisitionDate { get; init; }

    /// <summary>
    /// Zoom levels for tile pre-generation. Empty to skip tile generation.
    /// </summary>
    public int[] TileZoomLevels { get; init; } = [0, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>
    /// Power-of-two reduction factors for persisted overview pyramids (2 = half resolution,
    /// 4 = quarter, ...). The dynamic tile read path reuses the closest persisted level instead
    /// of recomputing an <c>ST_Rescale</c> reduction on every low-zoom request (#1836). Empty to
    /// skip overview persistence. Requires a known CRS (SRID &gt; 0) just like tile pre-generation.
    /// </summary>
    public int[] OverviewFactors { get; init; } = [2, 4, 8, 16];
}
