// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// Response for the ArcGIS ImageServer <c>find</c> operation.
/// </summary>
public sealed class ImageServerFindResponse
{
    /// <summary>
    /// Catalog images whose footprints contain the requested target geometry.
    /// </summary>
    [JsonPropertyName("images")]
    public ImageServerFindImage[] Images { get; init; } = [];
}

/// <summary>
/// Single image candidate returned by the ImageServer <c>find</c> operation.
/// </summary>
public sealed class ImageServerFindImage
{
    /// <summary>
    /// Raster catalog object identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>
    /// Raster catalog item URI or display name. Honua emits the catalog item name
    /// until raster source URI metadata is available.
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    /// <summary>
    /// Acquisition time in Unix milliseconds, when known.
    /// </summary>
    [JsonPropertyName("acquisitionDate")]
    public long? AcquisitionDate { get; init; }

    /// <summary>
    /// Center point of the catalog item footprint.
    /// </summary>
    [JsonPropertyName("center")]
    public ImageServerFindPoint? Center { get; init; }

    /// <summary>
    /// Approximate pixel size for the catalog item.
    /// </summary>
    [JsonPropertyName("pixelSize")]
    public double? PixelSize { get; init; }

    /// <summary>
    /// Raster row count.
    /// </summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>
    /// Raster column count.
    /// </summary>
    [JsonPropertyName("cols")]
    public int Cols { get; init; }
}

/// <summary>
/// Esri point with optional elevation and spatial reference for ImageServer find.
/// </summary>
public sealed class ImageServerFindPoint
{
    /// <summary>
    /// X coordinate.
    /// </summary>
    [JsonPropertyName("x")]
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate.
    /// </summary>
    [JsonPropertyName("y")]
    public required double Y { get; init; }

    /// <summary>
    /// Optional Z coordinate.
    /// </summary>
    [JsonPropertyName("z")]
    public double? Z { get; init; }

    /// <summary>
    /// Coordinate spatial reference.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public SpatialReference? SpatialReference { get; init; }
}
