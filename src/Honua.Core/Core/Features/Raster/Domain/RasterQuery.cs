// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct RasterQuery
{
    public RasterClipRegion? ClipRegion { get; init; }

    // When set, the export covers the clip envelope and pixels outside the footprint are NoData; otherwise the output is only the intersection.
    public bool CoverClipExtent { get; init; }

    public RasterClipRegion? RenderingClip { get; init; }

    public int? OutputSrid { get; init; }

    public int[]? Bands { get; init; }

    public PixelSize? PixelSize { get; init; }

    public ResamplingAlgorithm ResamplingAlgorithm { get; init; } = ResamplingAlgorithm.NearestNeighbor;

    public RasterFormat OutputFormat { get; init; } = RasterFormat.PNG;

    public int? Quality { get; init; }

    public TiffCompression? TiffCompression { get; init; }

    public int? OutputWidth { get; init; }

    public int? OutputHeight { get; init; }

    public RasterBandArithmetic? BandArithmetic { get; init; }

    public RasterTerrainFunction? Terrain { get; init; }

    public RasterStretch? Stretch { get; init; }

    public RasterColormap? Colormap { get; init; }

    public RasterQuery()
    {
    }
}

public readonly record struct PixelSize
{
    public required double Width { get; init; }

    public required double Height { get; init; }

    public PixelSize(double width, double height)
    {
        Width = width;
        Height = height;
    }
}

public readonly record struct RasterClipRegion
{
    public required byte[] Geometry { get; init; }

    public int? Srid { get; init; }

    public bool Inverted { get; init; }
}

public enum ResamplingAlgorithm
{
    NearestNeighbor = 0,

    Bilinear = 1,

    Bicubic = 2,

    Lanczos = 3
}

public enum RasterFormat
{
    PNG = 0,

    JPEG = 1,

    TIFF = 2,

    Raw = 3,

    COG = 4
}

public static class RasterFormatExtensions
{
    // Raw is not a GDAL driver and must be encoded without one.
    public static string ToGdalDriverName(this RasterFormat format) => format switch
    {
        RasterFormat.PNG => "PNG",
        RasterFormat.JPEG => "JPEG",
        RasterFormat.TIFF => "GTiff",
        RasterFormat.COG => "COG",
        RasterFormat.Raw => "Raw",
        _ => "PNG"
    };

    public static string ToContentType(this RasterFormat format) => format switch
    {
        RasterFormat.PNG => "image/png",
        RasterFormat.JPEG => "image/jpeg",
        RasterFormat.TIFF => "image/tiff",
        RasterFormat.COG => "image/tiff",
        _ => "application/octet-stream"
    };
}

public enum TiffCompression
{
    None = 0,

    LZW = 1,

    Deflate = 2,

    JPEG = 3
}
