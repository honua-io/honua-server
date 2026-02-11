// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Query specification for raster operations.
/// </summary>
public readonly record struct RasterQuery
{
    /// <summary>
    /// Optional clip region to spatially subset the raster output.
    /// </summary>
    public RasterClipRegion? ClipRegion { get; init; }

    /// <summary>
    /// Target spatial reference system for output.
    /// </summary>
    public int? OutputSrid { get; init; }

    /// <summary>
    /// Specific bands to retrieve (1-based indexing). If null, all bands are retrieved.
    /// </summary>
    public int[]? Bands { get; init; }

    /// <summary>
    /// Output pixel size for resampling.
    /// </summary>
    public PixelSize? PixelSize { get; init; }

    /// <summary>
    /// Resampling algorithm to use when scaling.
    /// </summary>
    public ResamplingAlgorithm ResamplingAlgorithm { get; init; } = ResamplingAlgorithm.NearestNeighbor;

    /// <summary>
    /// Output format for the raster data.
    /// </summary>
    public RasterFormat OutputFormat { get; init; } = RasterFormat.PNG;

    /// <summary>
    /// Quality settings for compressed formats (1-100).
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>
    /// Compression type for TIFF output.
    /// </summary>
    public TiffCompression? TiffCompression { get; init; }

    /// <summary>
    /// Desired output width in pixels for image export.
    /// When set, the raster will be resized to this width.
    /// </summary>
    public int? OutputWidth { get; init; }

    /// <summary>
    /// Desired output height in pixels for image export.
    /// When set, the raster will be resized to this height.
    /// </summary>
    public int? OutputHeight { get; init; }

    /// <summary>
    /// Initializes a new instance of the RasterQuery struct.
    /// </summary>
    public RasterQuery()
    {
    }
}

/// <summary>
/// Pixel size specification for raster resampling.
/// </summary>
public readonly record struct PixelSize
{
    /// <summary>
    /// Pixel width in coordinate units.
    /// </summary>
    public required double Width { get; init; }

    /// <summary>
    /// Pixel height in coordinate units.
    /// </summary>
    public required double Height { get; init; }

    /// <summary>
    /// Initializes a new instance of the PixelSize struct.
    /// </summary>
    public PixelSize(double width, double height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>
/// Region specification for clipping raster output.
/// </summary>
public readonly record struct RasterClipRegion
{
    /// <summary>
    /// Clip geometry in Well-Known Binary (WKB) format.
    /// </summary>
    public required byte[] Geometry { get; init; }

    /// <summary>
    /// SRID of the clip geometry. If null, assumes the raster's native SRID.
    /// </summary>
    public int? Srid { get; init; }
}

/// <summary>
/// Resampling algorithms for raster scaling.
/// </summary>
public enum ResamplingAlgorithm
{
    /// <summary>
    /// Nearest neighbor resampling (fastest, preserves categorical data).
    /// </summary>
    NearestNeighbor = 0,

    /// <summary>
    /// Bilinear interpolation (good for continuous data).
    /// </summary>
    Bilinear = 1,

    /// <summary>
    /// Bicubic interpolation (best quality for continuous data).
    /// </summary>
    Bicubic = 2,

    /// <summary>
    /// Lanczos resampling (high quality with sharpening).
    /// </summary>
    Lanczos = 3
}

/// <summary>
/// Output formats for raster data.
/// </summary>
public enum RasterFormat
{
    /// <summary>
    /// Portable Network Graphics (PNG) - lossless compression.
    /// </summary>
    PNG = 0,

    /// <summary>
    /// Joint Photographic Experts Group (JPEG) - lossy compression.
    /// </summary>
    JPEG = 1,

    /// <summary>
    /// Tagged Image File Format (TIFF) - supports various compression options.
    /// </summary>
    TIFF = 2,

    /// <summary>
    /// Raw pixel data as byte array.
    /// </summary>
    Raw = 3
}

/// <summary>
/// Extension methods for <see cref="RasterFormat"/> providing consistent
/// GDAL driver names and MIME content types across all layers.
/// </summary>
public static class RasterFormatExtensions
{
    /// <summary>
    /// Gets the GDAL driver name for the format (e.g., "PNG", "JPEG", "GTiff").
    /// </summary>
    public static string ToGdalDriverName(this RasterFormat format) => format switch
    {
        RasterFormat.PNG => "PNG",
        RasterFormat.JPEG => "JPEG",
        RasterFormat.TIFF => "GTiff",
        _ => "PNG"
    };

    /// <summary>
    /// Gets the MIME content type for the format (e.g., "image/png").
    /// </summary>
    public static string ToContentType(this RasterFormat format) => format switch
    {
        RasterFormat.PNG => "image/png",
        RasterFormat.JPEG => "image/jpeg",
        RasterFormat.TIFF => "image/tiff",
        _ => "application/octet-stream"
    };
}

/// <summary>
/// TIFF compression options.
/// </summary>
public enum TiffCompression
{
    /// <summary>
    /// No compression.
    /// </summary>
    None = 0,

    /// <summary>
    /// LZW lossless compression.
    /// </summary>
    LZW = 1,

    /// <summary>
    /// Deflate lossless compression.
    /// </summary>
    Deflate = 2,

    /// <summary>
    /// JPEG compression (lossy).
    /// </summary>
    JPEG = 3
}
