// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    /// Vertical unit for height and base values. Stored as the raw token
    /// from catalog metadata so unknown values flow through to
    /// <see cref="ExtrusionValidator"/> and surface as
    /// <see cref="ExtrusionErrorCodes.UnitUnrecognized"/> rather than
    /// throwing during catalog deserialization. Recognized values
    /// (<see cref="VerticalUnits"/>) are matched case-insensitively;
    /// null or whitespace defaults to meters.
    /// </summary>
    public string? Unit { get; init; }

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
/// Canonical wire tokens for vertical units recognized by the v1
/// extrusion contract. The tokens are stable across releases and are
/// emitted lowercase/camelCase regardless of the input casing.
/// </summary>
public static class VerticalUnits
{
    /// <summary>Metres (default).</summary>
    public const string Meters = "meters";

    /// <summary>International feet (0.3048 m).</summary>
    public const string Feet = "feet";

    /// <summary>US Survey feet (1200/3937 m).</summary>
    public const string UsSurveyFeet = "usSurveyFeet";

    /// <summary>
    /// Attempts to normalize a raw unit token to the canonical wire form.
    /// Returns true and the canonical token for recognized values
    /// (case-insensitive); returns true with <see cref="Meters"/> when the
    /// input is null or whitespace; returns false for any other token.
    /// </summary>
    public static bool TryNormalize(string? rawUnit, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            normalized = Meters;
            return true;
        }

        var trimmed = rawUnit.Trim();
        if (string.Equals(trimmed, Meters, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Meters;
            return true;
        }

        if (string.Equals(trimmed, Feet, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Feet;
            return true;
        }

        if (string.Equals(trimmed, UsSurveyFeet, StringComparison.OrdinalIgnoreCase))
        {
            normalized = UsSurveyFeet;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
