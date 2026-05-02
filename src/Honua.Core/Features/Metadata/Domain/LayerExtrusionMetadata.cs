// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Unified extrusion metadata derived from
/// <see cref="Honua.Core.Features.Catalog.Domain.LayerExtrusionInfo"/> and
/// surfaced as a typed property on <see cref="LayerMetadata"/>. The wire
/// representation uses a string for <see cref="Unit"/> so the metadata
/// contract stays stable if new unit values are added later without
/// forcing consumers to update enum definitions.
/// </summary>
public sealed record LayerExtrusionMetadata
{
    /// <summary>
    /// Whether extrusion is enabled for this layer. Always true when this
    /// record is present; the absence of <c>ExtrusionInfo</c> on
    /// <see cref="LayerMetadata"/> represents a 2D-only layer.
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
    /// Vertical unit for height and base values. Lowercase string on the
    /// wire (e.g. "meters", "feet", "usSurveyFeet").
    /// </summary>
    public string Unit { get; init; } = "meters";

    /// <summary>
    /// Fallback height when the height field value is null. Null when no
    /// fallback is configured.
    /// </summary>
    public double? DefaultHeight { get; init; }

    /// <summary>
    /// Optional material or style hint passed through to downstream
    /// 3D Tiles generation.
    /// </summary>
    public string? MaterialHint { get; init; }
}
