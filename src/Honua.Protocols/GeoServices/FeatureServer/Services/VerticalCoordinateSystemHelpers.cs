// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal static class VerticalCoordinateSystemHelpers
{
    public static bool RequestsVerticalTransform(string? spatialReferenceValue)
    {
        if (string.IsNullOrWhiteSpace(spatialReferenceValue))
        {
            return false;
        }

        var trimmed = spatialReferenceValue.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            return HasPositiveInt(root, "vcsWkid") || HasPositiveInt(root, "latestVcsWkid");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasPositiveInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.Number &&
           element.TryGetInt32(out var value) &&
           value > 0;
}
