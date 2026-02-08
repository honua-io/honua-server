// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.MapServer.Models;

/// <summary>
/// Esri spatial extent information for MapServer responses.
/// </summary>
internal sealed class EsriExtent
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    [JsonPropertyName("xmin")]
    public required double Xmin { get; init; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    [JsonPropertyName("ymin")]
    public required double Ymin { get; init; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    [JsonPropertyName("xmax")]
    public required double Xmax { get; init; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    [JsonPropertyName("ymax")]
    public required double Ymax { get; init; }

    /// <summary>
    /// Spatial reference for the extent.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public required EsriSpatialReference SpatialReference { get; init; }

    /// <summary>
    /// Creates an EsriExtent from a Core FeatureExtent.
    /// </summary>
    internal static EsriExtent FromFeatureExtent(FeatureExtent extent) => new()
    {
        Xmin = extent.MinX,
        Ymin = extent.MinY,
        Xmax = extent.MaxX,
        Ymax = extent.MaxY,
        SpatialReference = EsriSpatialReference.FromSpatialReference(extent.GetSpatialReference())
    };
}
