// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeaturePayloadReader
{
    private static readonly HashSet<string> FeatureContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        MediaTypes.GeoJson,
        MediaTypes.Json
    };

    private static readonly HashSet<string> PatchContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        MediaTypes.GeoJson,
        MediaTypes.Json,
        "application/merge-patch+json"
    };

    private static readonly HashSet<string> JsonContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        MediaTypes.Json
    };

    public static IResult? ValidateFeatureContentType(HttpContext context)
        => ValidateContentType(context, FeatureContentTypes);

    public static IResult? ValidatePatchContentType(HttpContext context)
        => ValidateContentType(context, PatchContentTypes);

    public static IResult? ValidateJsonContentType(HttpContext context)
        => ValidateContentType(context, JsonContentTypes);

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

    private static IResult? ValidateContentType(HttpContext context, HashSet<string> allowedContentTypes)
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        var contentType = context.Request.ContentType;
        var mediaType = string.IsNullOrWhiteSpace(contentType)
            ? null
            : contentType.Split(';', 2)[0].Trim();

        if (!string.IsNullOrWhiteSpace(mediaType) && allowedContentTypes.Contains(mediaType))
        {
            return null;
        }

        return ValidationErrorHelpers.CreateUnsupportedMediaType(
            context,
            string.IsNullOrWhiteSpace(mediaType) ? "(missing)" : mediaType,
            allowedContentTypes);
    }
}
