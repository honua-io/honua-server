// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Extrusion configuration stored with a feature layer definition.
/// Drives the v1 <c>extrusionInfo</c> contract surfaced through the
/// GeoServices FeatureServer layer metadata response and consumed by
/// downstream 3D Tiles generation.
/// </summary>
public sealed record LayerExtrusionInfo
{
    /// <summary>
    /// Name of the numeric field that drives extrusion height.
    /// Required and must reference a numeric field on the layer.
    /// </summary>
    public string HeightField { get; init; } = string.Empty;

    /// <summary>
    /// Optional field name for base (bottom) elevation. When null, the
    /// base elevation is the layer geometry footprint.
    /// </summary>
    public string? BaseHeightField { get; init; }

    /// <summary>
    /// Vertical unit for height and base values.
    /// </summary>
    public VerticalUnit Unit { get; init; } = VerticalUnit.Meters;

    /// <summary>
    /// Fallback height when the height field value is null. Must be &gt;= 0.
    /// When null, no fallback is applied.
    /// </summary>
    public double? DefaultHeight { get; init; }

    /// <summary>
    /// Optional material or style hint passed through to 3D Tiles
    /// generation (honua-server-842). Free-form string; not interpreted
    /// by this server.
    /// </summary>
    public string? MaterialHint { get; init; }
}

/// <summary>
/// Recognized vertical units for extrusion metadata.
/// Serialized as lowercase strings on the wire ("meters", "feet",
/// "usSurveyFeet"). Per-member <see cref="JsonStringEnumMemberNameAttribute"/>
/// pins the wire form so it stays stable regardless of enum identifier
/// changes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerticalUnit>))]
public enum VerticalUnit
{
    /// <summary>Metres (default).</summary>
    [JsonStringEnumMemberName("meters")]
    Meters,

    /// <summary>International feet (0.3048 m).</summary>
    [JsonStringEnumMemberName("feet")]
    Feet,

    /// <summary>US Survey feet (1200/3937 m).</summary>
    [JsonStringEnumMemberName("usSurveyFeet")]
    UsSurveyFeet
}
