// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.MapServer.Models;

/// <summary>
/// Parameters for the MapServer identify (click-query) operation.
/// </summary>
internal sealed class IdentifyParameters
{
    /// <summary>
    /// Click geometry in the format "x,y" for a point.
    /// </summary>
    public string? Geometry { get; init; }

    /// <summary>
    /// Type of the identify geometry (esriGeometryPoint, esriGeometryEnvelope, etc.).
    /// </summary>
    public string GeometryType { get; init; } = "esriGeometryPoint";

    /// <summary>
    /// Spatial reference of the identify geometry.
    /// </summary>
    public string? Sr { get; init; }

    /// <summary>
    /// Comma-separated list of layer IDs to identify.
    /// </summary>
    public string? Layers { get; init; }

    /// <summary>
    /// Pixel tolerance for point identification.
    /// </summary>
    public int Tolerance { get; init; } = 3;

    /// <summary>
    /// Current map extent as "xmin,ymin,xmax,ymax".
    /// </summary>
    public string? MapExtent { get; init; }

    /// <summary>
    /// Image display dimensions as "width,height,dpi".
    /// </summary>
    public string? ImageDisplay { get; init; }

    /// <summary>
    /// Whether to return geometry in the result.
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Output response format.
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Maximum number of features to return per layer.
    /// </summary>
    public int? MaxAllowableOffset { get; init; }
}
