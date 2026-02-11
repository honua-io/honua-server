// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Abstraction for server-side map rendering from raster and vector data.
/// Supports OGC API - Maps operations.
/// </summary>
public interface IRasterMapRenderer
{
    /// <summary>
    /// Renders a map from a single collection (layer) with optional styling.
    /// </summary>
    /// <param name="layerId">Layer identifier to render</param>
    /// <param name="request">Map rendering request with extent, size, and format options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rendered map image</returns>
    Task<RasterResult> RenderCollectionMapAsync(
        int layerId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a map from multiple collections (dataset-wide map).
    /// </summary>
    /// <param name="layerIds">Layer identifiers to render (in order)</param>
    /// <param name="request">Map rendering request with extent, size, and format options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rendered map image</returns>
    Task<RasterResult> RenderDatasetMapAsync(
        int[] layerIds,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a styled map using a specific style definition.
    /// </summary>
    /// <param name="layerId">Layer identifier to render</param>
    /// <param name="styleId">Style identifier to apply</param>
    /// <param name="request">Map rendering request with extent, size, and format options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rendered map image</returns>
    Task<RasterResult> RenderStyledMapAsync(
        int layerId,
        string styleId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request parameters for map rendering operations.
/// </summary>
public readonly record struct MapRenderRequest
{
    /// <summary>
    /// Bounding box for the map extent (in the specified CRS).
    /// Format: [minX, minY, maxX, maxY].
    /// </summary>
    public required double[] BoundingBox { get; init; }

    /// <summary>
    /// Coordinate reference system for the bounding box.
    /// </summary>
    public int? BoundingBoxCrs { get; init; }

    /// <summary>
    /// Output coordinate reference system for the map.
    /// </summary>
    public int? Crs { get; init; }

    /// <summary>
    /// Width of the output map in pixels.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Height of the output map in pixels.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>
    /// Output format for the map.
    /// </summary>
    public RasterFormat Format { get; init; } = RasterFormat.PNG;

    /// <summary>
    /// Whether to include a background (transparent if false).
    /// </summary>
    public bool Transparent { get; init; } = true;

    /// <summary>
    /// Background color (if not transparent).
    /// </summary>
    public string? BackgroundColor { get; init; }

    /// <summary>
    /// Temporal filter for time-enabled layers.
    /// </summary>
    public DateTimeOffset? DateTime { get; init; }

    /// <summary>
    /// Quality settings for compressed formats (1-100).
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>
    /// Initializes a new instance of the MapRenderRequest struct.
    /// </summary>
    public MapRenderRequest(double[] boundingBox, int width, int height)
    {
        BoundingBox = boundingBox;
        Width = width;
        Height = height;
    }
}
