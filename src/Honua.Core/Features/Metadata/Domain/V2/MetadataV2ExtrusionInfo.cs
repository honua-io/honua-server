// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Extrusion configuration stored on a Metadata v2 feature resource.
/// </summary>
public sealed record MetadataV2ExtrusionInfo
{
    /// <summary>
    /// Name of the numeric field that drives extrusion height.
    /// </summary>
    [JsonPropertyName("heightField")]
    public string HeightField { get; init; } = string.Empty;

    /// <summary>
    /// Optional field name for base elevation. When null, the base is the geometry footprint.
    /// </summary>
    [JsonPropertyName("baseHeightField")]
    public string? BaseHeightField { get; init; }

    /// <summary>
    /// Vertical unit for height and base values. Null or whitespace defaults to meters.
    /// </summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    /// <summary>
    /// Fallback height when the height field value is null. Must be greater than or equal to zero.
    /// </summary>
    [JsonPropertyName("defaultHeight")]
    public double? DefaultHeight { get; init; }

    /// <summary>
    /// Optional material or style hint passed through to 3D Tiles generation.
    /// </summary>
    [JsonPropertyName("materialHint")]
    public string? MaterialHint { get; init; }
}

/// <summary>
/// Canonical wire tokens for vertical units recognized by Metadata v2 extrusion.
/// </summary>
public static class MetadataV2VerticalUnits
{
    /// <summary>Meters.</summary>
    public const string Meters = "meters";

    /// <summary>International feet.</summary>
    public const string Feet = "feet";

    /// <summary>US survey feet.</summary>
    public const string UsSurveyFeet = "usSurveyFeet";

    /// <summary>
    /// Attempts to normalize a raw unit token to the canonical wire form.
    /// </summary>
    /// <param name="rawUnit">Raw unit token.</param>
    /// <param name="normalized">Canonical unit token when recognized.</param>
    /// <returns>True when the token is recognized or empty; otherwise false.</returns>
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
