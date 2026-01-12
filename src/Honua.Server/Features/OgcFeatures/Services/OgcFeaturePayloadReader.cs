// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.OgcFeatures.Services;

internal static class OgcFeaturePayloadReader
{
    public static async Task<(GeoJsonFeature? Feature, string? Error)> ReadGeoJsonFeatureAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength == 0)
            {
                return (null, "Request body is required.");
            }

            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "GeoJSON payload must be an object.");
            }

            if (!document.RootElement.TryGetProperty("type", out var typeProperty))
            {
                return (null, "GeoJSON payload must include a 'type' member.");
            }

            if (!string.Equals(typeProperty.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "GeoJSON 'type' must be 'Feature'.");
            }

            try
            {
                var feature = JsonSerializer.Deserialize<GeoJsonFeature>(document.RootElement, OgcJsonContext.Default.Options);
                return feature == null
                    ? (null, "Invalid GeoJSON payload.")
                    : (feature, null);
            }
            catch (JsonException)
            {
                return (null, "Invalid GeoJSON payload.");
            }
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }
}
