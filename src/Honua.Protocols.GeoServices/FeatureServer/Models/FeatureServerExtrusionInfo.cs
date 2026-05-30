// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Extrusion metadata for FeatureServer layers. Surfaced as the
/// <c>extrusionInfo</c> property on the layer metadata response when a
/// layer has 3D extrusion configured. Field-naming and null-handling
/// match the existing FeatureServer model conventions: the parent
/// <see cref="FeatureServerJsonContext"/> applies camelCase property
/// names and omits null properties on serialization, which preserves
/// byte-for-byte compatibility with 2D-only layers.
/// </summary>
public sealed class FeatureServerExtrusionInfo
{
    /// <summary>
    /// Whether extrusion is enabled for this layer. Always true when
    /// this object is present in the response.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Name of the numeric field that drives extrusion height.
    /// </summary>
    public string HeightField { get; init; } = string.Empty;

    /// <summary>
    /// Optional field name for base (bottom) elevation.
    /// </summary>
    public string? BaseHeightField { get; init; }

    /// <summary>
    /// Vertical unit for height and base values. Lowercase string on
    /// the wire (e.g. "meters", "feet", "usSurveyFeet").
    /// </summary>
    public string Unit { get; init; } = "meters";

    /// <summary>
    /// Fallback height when the height field value is null.
    /// </summary>
    public double? DefaultHeight { get; init; }

    /// <summary>
    /// Optional material or style hint passed through to downstream
    /// 3D Tiles generation.
    /// </summary>
    public string? MaterialHint { get; init; }
}
