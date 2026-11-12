// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Infrastructure.Rendering;

// The style services a server-rendered layer draws from: the catalog style and, when a converter
// is registered, the layer's saved drawingInfo.
internal readonly record struct RasterStyleSources(
    ILayerStyleCatalog Catalog,
    IGeoServicesStyleConverter? Converter);

internal static partial class RasterMapRenderingPipeline
{
    private const string SavedDrawingInfoExtensionKey = "geoservices:drawingInfo";
    private const string LegacyDrawingInfoExtensionKey = "drawingInfo";
    private static readonly ConcurrentDictionary<int, CachedSavedStyle> _savedDrawingInfoStyleCache = new();

    // A layer's saved presentation (the Console "Layer presentation" override, stored as the
    // resource's geoservices:drawingInfo extension) is the drawingInfo FeatureServer/MapServer
    // metadata advertise, so server-rendered images and legends draw it too. Without an override,
    // or when the saved renderer converts to nothing renderable, the catalog style applies.
    internal static async Task<RasterStylePlan> GetRasterStylePlanAsync(
        RasterStyleSources styleSources,
        MetadataV2Resource resource,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (styleSources.Converter is { } styleConverter
            && ResolveSavedDrawingInfoStyle(resource, storageLayerId, styleConverter) is { } saved)
        {
            return saved.Plan;
        }

        return await GetRasterStylePlanAsync(styleSources.Catalog, storageLayerId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool TryGetSavedDrawingInfo(MetadataV2Resource resource, out JsonElement drawingInfo)
    {
        if (resource.Extensions is { } extensions
            && (extensions.TryGetValue(SavedDrawingInfoExtensionKey, out drawingInfo)
                || extensions.TryGetValue(LegacyDrawingInfoExtensionKey, out drawingInfo))
            && drawingInfo.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        drawingInfo = default;
        return false;
    }

    // The MapLibre JSON a saved drawingInfo converts to, for renderers that take a style document.
    internal static string? ResolveSavedDrawingInfoMapLibreStyle(
        MetadataV2Resource resource,
        int storageLayerId,
        IGeoServicesStyleConverter styleConverter)
        => ResolveSavedDrawingInfoStyle(resource, storageLayerId, styleConverter)?.MapLibreStyleJson;

    private static ConvertedSavedStyle? ResolveSavedDrawingInfoStyle(
        MetadataV2Resource resource,
        int storageLayerId,
        IGeoServicesStyleConverter styleConverter)
    {
        if (!TryGetSavedDrawingInfo(resource, out var drawingInfo))
        {
            return null;
        }

        // Conversion depends on the geometry type and layer name as well as the document.
        var key = new SavedStyleKey(
            storageLayerId,
            resource.ReadGeometryType(),
            resource.Metadata.Name,
            drawingInfo.GetRawText());
        if (_savedDrawingInfoStyleCache.TryGetValue(storageLayerId, out var cached) && cached.Key == key)
        {
            return cached.Style;
        }

        var converted = ConvertSavedDrawingInfo(drawingInfo, key, styleConverter);
        _savedDrawingInfoStyleCache[storageLayerId] = converted;
        return converted.Style;
    }

    private static CachedSavedStyle ConvertSavedDrawingInfo(
        JsonElement drawingInfo,
        SavedStyleKey key,
        IGeoServicesStyleConverter styleConverter)
    {
        try
        {
            var conversion = styleConverter.Convert(drawingInfo, key.StorageLayerId, key.LayerName, key.GeometryType);
            var plan = BuildRasterStylePlanFromJson(conversion.MapLibreStyleJson);
            return new CachedSavedStyle(key, plan.StyleLayers.Length > 0
                ? new ConvertedSavedStyle(conversion.MapLibreStyleJson, plan)
                : null);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // The admin endpoint stores any JSON object; one the converter cannot handle must not
            // fail every render of the layer, so it falls back to the catalog style.
            return new CachedSavedStyle(key, null);
        }
    }

    private readonly record struct SavedStyleKey(
        int StorageLayerId,
        MetadataV2GeometryType GeometryType,
        string LayerName,
        string DrawingInfoJson);

    private sealed record ConvertedSavedStyle(string MapLibreStyleJson, RasterStylePlan Plan);

    private sealed record CachedSavedStyle(SavedStyleKey Key, ConvertedSavedStyle? Style);
}
