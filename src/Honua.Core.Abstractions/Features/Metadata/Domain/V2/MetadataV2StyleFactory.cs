// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2StyleFactory
{
    public const string StyleResourceIdPrefix = "style-";

    public static string BuildStyleResourceId(string styleId)
        => StyleResourceIdPrefix + SanitizeId(styleId);

    public static MetadataV2Object BuildStyleResource(
        string styleId,
        string mapLibreStyleJson,
        string? title,
        string? description,
        string? drawingInfoJson,
        int styleVersion,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapLibreStyleJson);

        var encodings = new List<MetadataV2StyleEncoding>(2)
        {
            new()
            {
                Encoding = "mapbox-style",
                Body = mapLibreStyleJson,
                ContentType = "application/vnd.mapbox.style+json"
            }
        };

        if (!string.IsNullOrWhiteSpace(drawingInfoJson))
        {
            encodings.Add(new MetadataV2StyleEncoding
            {
                Encoding = "esri-drawing-info",
                Body = drawingInfoJson,
                ContentType = "application/json"
            });
        }

        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? styleId : title;
        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildStyleResourceId(styleId),
                Name = styleId,
                Title = resolvedTitle,
                Description = description,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            },
            Type = MetadataV2ObjectType.Style,
            Style = new MetadataV2Style
            {
                Title = resolvedTitle,
                Abstract = description,
                StyleVersion = styleVersion,
                Encodings = encodings
            },
            Status = new MetadataV2Status
            {
                Lifecycle = MetadataV2LifecycleStatus.Active,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = updatedAt
            }
        };
    }

    private static string SanitizeId(string value)
    {
        var trimmed = value.Trim();
        var chars = trimmed.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? ch
                : '-');
        var sanitized = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}
