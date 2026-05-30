// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.MapServer.Models;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const int DefaultLegendSwatchWidth = 20;
    private const int DefaultLegendSwatchHeight = 20;

    private sealed record LegendLayerDescriptor(
        int LayerId,
        string LayerName,
        MetadataV2GeometryType GeometryType,
        double? MinScale,
        double? MaxScale,
        MetadataV2Resource Resource);

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
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            ServiceProtocols.MapServer,
            context,
            cancellationToken: cancellationToken);
        if (!serviceResult.IsValid)
        {
            return serviceResult.ErrorResult!;
        }

        var service = serviceResult.Service!;
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var legendLayerDescriptors = ResolveLegendLayers(snapshot, service);

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            legendLayerDescriptors.Select(static layer => layer.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        if (!TryParseLegendSwatchSize(context.Request.Query, out var swatchWidth, out var swatchHeight, out var sizeError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, sizeError ?? "Invalid size parameter.");
        }

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var knownLayerIds = legendLayerDescriptors
            .Select(static layer => layer.LayerId)
            .ToHashSet();
        if (!TryParseDynamicLayers(context.Request.Query.TryGetValue("dynamicLayers", out var dlValues) ? dlValues.ToString() : null, knownLayerIds, queryValidator, out var dynamicLayers, out var dynamicLayersError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                dynamicLayersError ?? "Invalid dynamicLayers parameter.");
        }

        LegendLayerDescriptor[] visibleLayers;
        if (dynamicLayers.Count > 0)
        {
            var layerLookup = legendLayerDescriptors.ToDictionary(static layer => layer.LayerId);
            visibleLayers = dynamicLayers
                .Where(dynamicLayer => layerLookup.ContainsKey(dynamicLayer.MapLayerId))
                .Select(dynamicLayer => layerLookup[dynamicLayer.MapLayerId])
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();
        }
        else
        {
            visibleLayers = legendLayerDescriptors
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();
        }

        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var legendLayers = new List<LegendLayerInfo>();

        foreach (var layer in visibleLayers)
        {
            var style = await styleCatalog.GetLayerStyleAsync(layer.LayerId, cancellationToken);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

            var entries = BuildLegendEntries(styleLayers, layer.GeometryType, swatchWidth, swatchHeight);

            legendLayers.Add(new LegendLayerInfo
            {
                LayerId = layer.LayerId,
                LayerName = layer.LayerName,
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

    private static LegendLayerDescriptor[] ResolveLegendLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<LegendLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int layerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            descriptors.Add(new LegendLayerDescriptor(
                layerId,
                string.IsNullOrWhiteSpace(resource.Metadata.Name)
                    ? publication.Metadata.Name
                    : resource.Metadata.Name,
                resource.ReadGeometryType(),
                resource.Display?.MinScale,
                resource.Display?.MaxScale,
                resource));
        }

        return [.. descriptors.OrderBy(static layer => layer.LayerId)];
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
        MetadataV2GeometryType geometryType,
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

            var swatchBytes = SkiaMapRenderer.RenderLegendSwatch(
                styleLayer,
                geometryType,
                swatchWidth,
                swatchHeight);

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

    private static string? MapGeometryTypeToLayerType(MetadataV2GeometryType geometryType)
    {
        return geometryType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => "Feature Layer",
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => "Feature Layer",
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => "Feature Layer",
            _ => "Feature Layer"
        };
    }
}
