// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Builds <see cref="MetadataV2ResourceType.Style"/> resources for the canonical graph
/// from independent-catalog style bytes (ADR-0048, Phase 2, issue #1389). Kept in
/// <c>Honua.Core.Abstractions</c> so both the publish-time graph producer and the
/// style-update graph sync produce identical Style resources without duplicating the
/// encoding-projection logic.
/// </summary>
public static class MetadataV2StyleResourceFactory
{
    /// <summary>The canonical resource id prefix for a style resource.</summary>
    public const string StyleResourceIdPrefix = "style-";

    /// <summary>
    /// Builds the canonical resource id for a catalog style id.
    /// </summary>
    /// <param name="styleId">Stable catalog style identifier.</param>
    /// <returns>The sanitized style resource id.</returns>
    public static string BuildStyleResourceId(string styleId)
        => StyleResourceIdPrefix + SanitizeId(styleId);

    /// <summary>
    /// Builds a <see cref="MetadataV2ResourceType.Style"/> resource from catalog style
    /// bytes. The canonical MapLibre body is always emitted as a <c>mapbox-style</c>
    /// encoding; a cached drawingInfo, when present, is emitted as
    /// <c>esri-drawing-info</c>. Other encodings (SLD) are derived on demand by the
    /// read path, so they are not pre-materialized here.
    /// </summary>
    /// <param name="styleId">Stable catalog style identifier (becomes the resource name).</param>
    /// <param name="mapLibreStyleJson">Canonical MapLibre style JSON.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="drawingInfoJson">Optional cached GeoServices drawingInfo JSON.</param>
    /// <param name="styleVersion">Author-managed integer style version.</param>
    /// <param name="createdAt">Resource created timestamp.</param>
    /// <param name="updatedAt">Resource updated timestamp.</param>
    /// <returns>The style resource.</returns>
    public static MetadataV2Resource BuildStyleResource(
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
        return new MetadataV2Resource
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
            Type = MetadataV2ResourceType.Style,
            Style = new MetadataV2ResourceStyle
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
