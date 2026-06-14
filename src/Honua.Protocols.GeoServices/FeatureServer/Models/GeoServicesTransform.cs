// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Esri featureSet coordinate quantization transform. Quantized geometry coordinates
/// are integer grid values relative to this transform: a client recovers world
/// coordinates with <c>x = translate[0] + qx * scale[0]</c> and, for
/// <c>upperLeft</c>, <c>y = translate[1] - qy * scale[1]</c>.
/// </summary>
public sealed class GeoServicesTransform
{
    /// <summary>Origin position: <c>upperLeft</c> (default) or <c>lowerLeft</c>.</summary>
    [JsonPropertyName("originPosition")]
    public required string OriginPosition { get; init; }

    /// <summary>Per-axis grid cell size <c>[scaleX, scaleY]</c> in map units.</summary>
    [JsonPropertyName("scale")]
    public required double[] Scale { get; init; }

    /// <summary>Origin offset <c>[translateX, translateY]</c> in map units.</summary>
    [JsonPropertyName("translate")]
    public required double[] Translate { get; init; }
}
