// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.MapServer.Models;

/// <summary>
/// Parameters for the MapServer export (map image generation) operation.
/// </summary>
internal sealed class ExportParameters
{
    /// <summary>
    /// Bounding box in the format "xmin,ymin,xmax,ymax".
    /// </summary>
    public string? Bbox { get; init; }

    /// <summary>
    /// Image size in the format "width,height" (pixels).
    /// </summary>
    public string? Size { get; init; }

    /// <summary>
    /// Output image DPI (default 96).
    /// </summary>
    public int Dpi { get; init; } = 96;

    /// <summary>
    /// Output image format (png, png8, png24, png32, jpg, gif).
    /// </summary>
    public string Format { get; init; } = "png";

    /// <summary>
    /// Whether the background should be transparent.
    /// </summary>
    public bool Transparent { get; init; } = true;

    /// <summary>
    /// Comma-separated list of visible layer IDs, or "show:0,1", "hide:2".
    /// </summary>
    public string? Layers { get; init; }

    /// <summary>
    /// Spatial reference of the bounding box (input SR).
    /// </summary>
    public string? BboxSr { get; init; }

    /// <summary>
    /// Spatial reference of the output image.
    /// </summary>
    public string? ImageSr { get; init; }

    /// <summary>
    /// Output response format.
    /// </summary>
    public string F { get; init; } = "image";

    /// <summary>
    /// Background color as hex string (e.g., "#ffffff").
    /// </summary>
    public string? BackgroundColor { get; init; }
}
