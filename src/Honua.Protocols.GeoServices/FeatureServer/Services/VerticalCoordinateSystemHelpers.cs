// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Helpers for detecting vertical coordinate system (VCS) requests in Esri spatial
/// reference parameters. Honua does not yet perform vertical datum transformations
/// (NAVD88 ↔ ellipsoidal geoid models); these helpers let the query path detect a
/// requested vertical transform and fail explicitly instead of silently dropping it.
/// </summary>
internal static class VerticalCoordinateSystemHelpers
{
    /// <summary>
    /// Determines whether the supplied Esri spatial-reference value requests a vertical
    /// transformation by carrying a vertical CS WKID (<c>vcsWkid</c> or
    /// <c>latestVcsWkid</c>) in its JSON object form.
    /// </summary>
    /// <param name="spatialReferenceValue">The raw outSR/inSR parameter value.</param>
    /// <returns>
    /// <see langword="true"/> when a non-zero vertical CS WKID is present; otherwise
    /// <see langword="false"/> (including bare WKID/WKT forms, which carry no VCS).
    /// </returns>
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
