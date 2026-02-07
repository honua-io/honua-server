// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Models;
using Honua.Server.Features.MapServer.Rendering;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer legend requests.
    /// </summary>
    private static async Task<IResult> HandleLegend(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        MapServerLog.LegendRequested(logger, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        var visibleLayers = service.Layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
            .ToArray();

        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var legendLayers = new List<LegendLayerInfo>();

        foreach (var layer in visibleLayers)
        {
            var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

            var entries = BuildLegendEntries(styleLayers, layer.GeometryType);

            legendLayers.Add(new LegendLayerInfo
            {
                LayerId = layer.Id,
                LayerName = layer.Name,
                LayerType = MapGeometryTypeToLayerType(layer.GeometryType),
                MinScale = layer.MinScale,
                MaxScale = layer.MaxScale,
                Legend = [.. entries]
            });
        }

        MapServerLog.LegendCompleted(logger, serviceId, legendLayers.Count);

        var response = new LegendResponse { Layers = [.. legendLayers] };
        return Results.Json(response, MapServerJsonContext.Default.LegendResponse, contentType: "application/json");
    }

    private static List<LegendEntry> BuildLegendEntries(
        MapLibreStyleLayer[] styleLayers,
        GeometryType geometryType)
    {
        var entries = new List<LegendEntry>();

        if (styleLayers.Length == 0)
        {
            // Generate default legend swatch
            var defaultSwatch = SkiaMapRenderer.RenderLegendSwatch(
                new MapLibreStyleLayer { Type = "default" },
                geometryType);

            entries.Add(new LegendEntry
            {
                Label = "Default",
                ImageData = Convert.ToBase64String(defaultSwatch),
                ContentType = "image/png",
                Width = 20,
                Height = 20
            });

            return entries;
        }

        foreach (var styleLayer in styleLayers)
        {
            if (styleLayer.Type == null || styleLayer.Type == "background")
            {
                continue;
            }

            var swatchBytes = SkiaMapRenderer.RenderLegendSwatch(styleLayer, geometryType);

            entries.Add(new LegendEntry
            {
                Label = styleLayer.Id ?? styleLayer.Type,
                ImageData = Convert.ToBase64String(swatchBytes),
                ContentType = "image/png",
                Width = 20,
                Height = 20
            });
        }

        return entries;
    }

    private static string? MapGeometryTypeToLayerType(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => "Feature Layer",
            GeometryType.LineString or GeometryType.MultiLineString => "Feature Layer",
            GeometryType.Polygon or GeometryType.MultiPolygon => "Feature Layer",
            _ => "Feature Layer"
        };
    }
}
