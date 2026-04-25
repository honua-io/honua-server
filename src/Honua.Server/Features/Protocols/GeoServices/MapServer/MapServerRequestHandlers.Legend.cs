// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.GeoServices.MapServer.Models;
using Honua.Server.Features.Infrastructure.Rendering;

namespace Honua.Server.Features.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const int DefaultLegendSwatchWidth = 20;
    private const int DefaultLegendSwatchHeight = 20;

    /// <summary>
    /// Handle MapServer legend requests.
    /// </summary>
    private static async Task<IResult> HandleLegend(HttpContext context)
    {
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        MapServerLog.LegendRequested(logger, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
        if (protocolError is not null)
        {
            return protocolError;
        }

        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        if (!TryParseLegendSwatchSize(context.Request.Query, out var swatchWidth, out var swatchHeight, out var sizeError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, sizeError ?? "Invalid size parameter.");
        }

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryParseDynamicLayers(context.Request.Query.TryGetValue("dynamicLayers", out var dlValues) ? dlValues.ToString() : null, service, queryValidator, out var dynamicLayers, out var dynamicLayersError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                dynamicLayersError ?? "Invalid dynamicLayers parameter.");
        }

        LayerDefinition[] visibleLayers;
        if (dynamicLayers.Count > 0)
        {
            var layerLookup = service.Layers.ToDictionary(l => l.Id);
            visibleLayers = dynamicLayers
                .Where(dl => layerLookup.ContainsKey(dl.MapLayerId))
                .Select(dl => layerLookup[dl.MapLayerId])
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
                .ToArray();
        }
        else
        {
            visibleLayers = service.Layers
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
                .ToArray();
        }

        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var legendLayers = new List<LegendLayerInfo>();

        foreach (var layer in visibleLayers)
        {
            var style = await styleCatalog.GetLayerStyleAsync(layer.Id, cancellationToken);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

            var entries = BuildLegendEntries(styleLayers, layer.GeometryType, swatchWidth, swatchHeight);

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

    private static bool TryParseLegendSwatchSize(
        IQueryCollection query,
        out int width,
        out int height,
        out string? error)
    {
        width = DefaultLegendSwatchWidth;
        height = DefaultLegendSwatchHeight;
        error = null;

        if (query.TryGetValue("size", out var sizeValues))
        {
            var sizeValue = sizeValues.ToString();
            if (!string.IsNullOrWhiteSpace(sizeValue))
            {
                var parts = sizeValue.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
                    {
                        error = "Invalid size width.";
                        return false;
                    }

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0)
                    {
                        error = "Invalid size height.";
                        return false;
                    }
                }
                else if (parts.Length == 1)
                {
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
                    {
                        error = "Invalid size parameter.";
                        return false;
                    }

                    height = width;
                }
                else
                {
                    error = "Invalid size parameter. Expected format: size or width,height.";
                    return false;
                }
            }
        }

        return true;
    }

    private static List<LegendEntry> BuildLegendEntries(
        MapLibreStyleLayer[] styleLayers,
        GeometryType geometryType,
        int swatchWidth = DefaultLegendSwatchWidth,
        int swatchHeight = DefaultLegendSwatchHeight)
    {
        var entries = new List<LegendEntry>();

        if (styleLayers.Length == 0)
        {
            // Generate default legend swatch
            var defaultSwatch = SkiaMapRenderer.RenderLegendSwatch(
                new MapLibreStyleLayer { Type = "default" },
                geometryType,
                swatchWidth,
                swatchHeight);

            entries.Add(new LegendEntry
            {
                Label = "Default",
                ImageData = Convert.ToBase64String(defaultSwatch),
                ContentType = "image/png",
                Width = swatchWidth,
                Height = swatchHeight
            });

            return entries;
        }

        foreach (var styleLayer in styleLayers)
        {
            if (styleLayer.Type == null || styleLayer.Type == "background")
            {
                continue;
            }

            var swatchBytes = SkiaMapRenderer.RenderLegendSwatch(styleLayer, geometryType, swatchWidth, swatchHeight);

            entries.Add(new LegendEntry
            {
                Label = styleLayer.Id ?? styleLayer.Type,
                ImageData = Convert.ToBase64String(swatchBytes),
                ContentType = "image/png",
                Width = swatchWidth,
                Height = swatchHeight
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
