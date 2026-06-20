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
    /// Optional additional clip region from a renderingRule <c>Clip</c> raster function.
    /// Applied as a second mask after <see cref="ClipRegion"/> so a non-rectangular
    /// area-of-interest can refine the output window. When <c>null</c> no extra clip applies.
    /// </summary>
    public RasterClipRegion? RenderingClip { get; init; }

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
    /// Optional two-raster band-arithmetic operation (e.g. NDVI) applied to the
    /// selected bands before <see cref="Stretch"/>. When set, the raster store
    /// composes a single analytic band from the two source bands using a vetted,
    /// hardcoded formula. When <c>null</c> no band arithmetic is applied.
    /// </summary>
    public RasterBandArithmetic? BandArithmetic { get; init; }

    /// <summary>
    /// Optional inline terrain (focal) raster function (hillshade/slope/aspect) applied
    /// to a single elevation band before <see cref="Stretch"/>. Distinct from the
    /// per-pixel <see cref="BandArithmetic"/> case: it reads a 3x3 neighbourhood via a
    /// vetted PostGIS surface function. <see cref="BandArithmetic"/> and this property are
    /// mutually exclusive. When <c>null</c> no terrain function is applied.
    /// </summary>
    public RasterTerrainFunction? Terrain { get; init; }

    /// <summary>
    /// Optional display stretch applied to pixel values before encoding. When
    /// set, each selected band is linearly rescaled to the 8-bit display range
    /// using bounds derived from the chosen <see cref="RasterStretchType"/>.
    /// When <c>null</c> the raw pixel values are encoded unchanged.
    /// </summary>
    public RasterStretch? Stretch { get; init; }

    /// <summary>
    /// Optional pseudocolour colormap applied after <see cref="Stretch"/> to map
    /// single-band pixel values to an RGBA image. When <c>null</c> no colormap is
    /// applied.
    /// </summary>
    public RasterColormap? Colormap { get; init; }

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

    /// <summary>
    /// When <c>true</c>, the clip is inverted: pixels <em>inside</em> the geometry are
    /// removed and everything outside is kept (Esri <c>Clip</c> raster function with
    /// <c>ClippingType=1</c>, "clip inside / keep outside"). When <c>false</c> (default)
    /// the raster is masked to the geometry, keeping pixels inside it.
    /// </summary>
    public bool Inverted { get; init; }
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
    Raw = 3,

    /// <summary>
    /// Cloud-Optimized GeoTIFF with internal tiling and overview pyramids.
    /// </summary>
    COG = 4
}

/// <summary>
/// Extension methods for <see cref="RasterFormat"/> providing consistent
/// GDAL driver names and MIME content types across all layers.
/// </summary>
public static class RasterFormatExtensions
{
    /// <summary>
    /// Gets the GDAL driver name for the format (e.g., "PNG", "JPEG", "GTiff", "COG").
    /// </summary>
    public static string ToGdalDriverName(this RasterFormat format) => format switch
    {
        RasterFormat.PNG => "PNG",
        RasterFormat.JPEG => "JPEG",
        RasterFormat.TIFF => "GTiff",
        RasterFormat.COG => "COG",
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
        RasterFormat.COG => "image/tiff",
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
