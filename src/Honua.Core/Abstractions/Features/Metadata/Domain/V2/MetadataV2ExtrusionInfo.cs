// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2ExtrusionInfo
{
    [JsonPropertyName("heightField")]
    public string HeightField { get; init; } = string.Empty;

    [JsonPropertyName("baseHeightField")]
    public string? BaseHeightField { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    [JsonPropertyName("defaultHeight")]
    public double? DefaultHeight { get; init; }

    [JsonPropertyName("materialHint")]
    public string? MaterialHint { get; init; }
}

public static class MetadataV2VerticalUnits
{
    public const string Meters = "meters";

    public const string Feet = "feet";

    public const string UsSurveyFeet = "usSurveyFeet";

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
