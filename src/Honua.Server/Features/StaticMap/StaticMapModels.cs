// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

/// <summary>
/// Parsed parameters for a static map image request.
/// </summary>
internal sealed class StaticMapParameters
{
    /// <summary>Center longitude.</summary>
    public double? Lon { get; init; }

    /// <summary>Center latitude.</summary>
    public double? Lat { get; init; }

    /// <summary>Zoom level (0–22).</summary>
    public int? Zoom { get; init; }

    /// <summary>Bounding box: xmin,ymin,xmax,ymax.</summary>
    public string? Bbox { get; init; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Output format (png, jpeg, webp).</summary>
    public string Format { get; init; } = "png";

    /// <summary>Output DPI (72, 150, 300).</summary>
    public int Dpi { get; init; } = 72;

    /// <summary>Comma-separated list of layer ids to render.</summary>
    public string? Layers { get; init; }

    /// <summary>Marker overlay specifications: lon,lat,color|…</summary>
    public string? Markers { get; init; }

    /// <summary>Path overlay specification: lon1,lat1|lon2,lat2|…</summary>
    public string? Path { get; init; }

    /// <summary>Attribute filter expression applied to feature layers.</summary>
    public string? Filter { get; init; }
}

/// <summary>
/// Edition limits for the static map image API.
/// </summary>
internal static class StaticMapEditionLimits
{
    /// <summary>Maximum image dimension for Community edition.</summary>
    public const int CommunityMaxDimension = 1280;

    /// <summary>Maximum DPI for Community edition.</summary>
    public const int CommunityMaxDpi = 72;

    /// <summary>Maximum image dimension for Pro/Enterprise editions.</summary>
    public const int ProMaxDimension = 4096;

    /// <summary>Maximum DPI for Pro/Enterprise editions.</summary>
    public const int ProMaxDpi = 300;

    /// <summary>Maximum number of markers for Community edition.</summary>
    public const int CommunityMaxMarkers = 10;

    /// <summary>Maximum number of path vertices for Community edition.</summary>
    public const int CommunityMaxPathVertices = 20;

    /// <summary>Maximum number of markers for Pro/Enterprise editions.</summary>
    public const int ProMaxMarkers = 100;

    /// <summary>Maximum number of path vertices for Pro/Enterprise editions.</summary>
    public const int ProMaxPathVertices = 500;
}

/// <summary>
/// A single marker overlay to render on the static map.
/// </summary>
internal readonly record struct MapMarker(double Lon, double Lat, string Color);

/// <summary>
/// A path overlay (polyline) to render on the static map.
/// </summary>
internal sealed class MapPath
{
    /// <summary>Ordered vertices of the path.</summary>
    public required (double Lon, double Lat)[] Vertices { get; init; }

    /// <summary>Stroke color (CSS hex or named).</summary>
    public string Color { get; init; } = "#FF0000";

    /// <summary>Stroke width in pixels.</summary>
    public float StrokeWidth { get; init; } = 2f;
}
