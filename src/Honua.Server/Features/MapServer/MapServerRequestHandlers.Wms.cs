// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Rendering;
using Honua.ServiceDefaults;
using SkiaSharp;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int WmsMaxImageDimension = 4096;
    private const int WmsDefaultWidth = 256;
    private const int WmsDefaultHeight = 256;

    /// <summary>
    /// Handle OGC WMS requests (GetCapabilities, GetMap).
    /// </summary>
    private static async Task<IResult> HandleWms(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var query = context.Request.Query;
            var service = query.TryGetValue("SERVICE", out var svc) ? svc.ToString() : null;
            var requestType = query.TryGetValue("REQUEST", out var req) ? req.ToString() : null;

            if (!string.Equals(service, "WMS", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(service))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "SERVICE must be WMS.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            var svcDef = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, svcDef.Layers, svcDef);
            if (accessError != null)
            {
                return accessError;
            }

            if (string.Equals(requestType, "GetMap", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmsGetMap(context, svcDef, serviceId, logger);
            }

            // Default to GetCapabilities
            MapServerLog.WmsRequested(logger, serviceId, "GetCapabilities");
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var xml = BuildWmsCapabilities(svcDef, serviceId, baseUrl);
            return Results.Text(xml, "application/xml", Encoding.UTF8);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.WmsFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "WMS request failed.");
        }
    }

    private static async Task<IResult> HandleWmsGetMap(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger)
    {
        MapServerLog.WmsRequested(logger, serviceId, "GetMap");
        var stopwatch = Stopwatch.StartNew();
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerExport, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wms-getmap");

        var query = context.Request.Query;
        var bboxValue = query.TryGetValue("BBOX", out var bb) ? bb.ToString() : null;
        if (!TryParseBbox(bboxValue, out var extent))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid or missing BBOX parameter. Expected format: xmin,ymin,xmax,ymax");
        }

        // Parse CRS/SRS
        var crsValue = query.TryGetValue("CRS", out var crs) ? crs.ToString() : null;
        if (string.IsNullOrWhiteSpace(crsValue))
        {
            crsValue = query.TryGetValue("SRS", out var srs) ? srs.ToString() : null;
        }

        var bboxSrid = TryParseWmsCrs(crsValue) ?? service.SpatialReference.Srid;

        // Parse dimensions
        var widthValue = query.TryGetValue("WIDTH", out var w) ? w.ToString() : null;
        var heightValue = query.TryGetValue("HEIGHT", out var h) ? h.ToString() : null;

        if (!int.TryParse(widthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageWidth) || imageWidth <= 0)
        {
            imageWidth = WmsDefaultWidth;
        }

        if (!int.TryParse(heightValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageHeight) || imageHeight <= 0)
        {
            imageHeight = WmsDefaultHeight;
        }

        imageWidth = Math.Clamp(imageWidth, 1, WmsMaxImageDimension);
        imageHeight = Math.Clamp(imageHeight, 1, WmsMaxImageDimension);

        // Parse FORMAT
        var format = query.TryGetValue("FORMAT", out var fmt) ? fmt.ToString() : "image/png";
        var imageFormat = "png";
        if (format.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            imageFormat = "jpg";
        }

        // Parse TRANSPARENT
        var transparentValue = query.TryGetValue("TRANSPARENT", out var tr) ? tr.ToString() : null;
        var transparent = string.Equals(transparentValue, "true", StringComparison.OrdinalIgnoreCase) ||
                          string.IsNullOrWhiteSpace(transparentValue);

        // Parse LAYERS
        var layersParam = query.TryGetValue("LAYERS", out var layers) ? layers.ToString() : null;

        // Transform extent if needed
        var transformResult = await TryTransformExtentAsync(
            context, extent, bboxSrid, bboxSrid, context.RequestAborted);
        if (!transformResult.IsSuccess)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                transformResult.Error ?? "Invalid spatial reference.");
        }

        var renderExtent = transformResult.Extent;

        // Resolve visible layers
        var renderLayers = ResolveWmsLayers(service, layersParam, context);

        if (renderLayers.Length == 0)
        {
            using var renderer = new SkiaMapRenderer();
            var emptyImage = renderer.RenderMap(
                [],
                [],
                renderExtent,
                imageWidth,
                imageHeight,
                transparent,
                null,
                GeometryType.None);

            stopwatch.Stop();
            HonuaTelemetry.SetSuccess(activity, 0);
            var emptyContentType = SkiaMapRenderer.GetContentType(imageFormat);
            return Results.Bytes(emptyImage, emptyContentType);
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

        var spatialFilter = CreateBboxSpatialFilter(renderExtent, bboxSrid);
        var totalFeatureCount = 0;
        var mapConfig = service.Metadata?.MapServer;
        var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;

        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface.");
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        var transformFn = SkiaMapRenderer.BuildTransform(renderExtent, imageWidth, imageHeight);

        foreach (var layer in renderLayers)
        {
            context.RequestAborted.ThrowIfCancellationRequested();

            if (!layer.HasGeometry)
            {
                continue;
            }

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = service.SpatialReference.Srid,
                OutputSrid = bboxSrid,
                Limit = maxFeatures
            };

            var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            totalFeatureCount += queryResult.Items.Length;

            var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

            RenderLayerToCanvas(canvas, queryResult.Items, styleLayers, transformFn, layer.GeometryType);
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, imageFormat);

        stopwatch.Stop();
        HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
        HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

        var contentType = SkiaMapRenderer.GetContentType(imageFormat);
        return Results.Bytes(imageBytes, contentType);
    }

    private static LayerDefinition[] ResolveWmsLayers(
        ServiceDefinition service,
        string? layerList,
        HttpContext context)
    {
        var accessibleLayers = service.Layers
            .Where(l => l.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        if (string.IsNullOrWhiteSpace(layerList))
        {
            return accessibleLayers.Where(l => l.DefaultVisibility).ToArray();
        }

        var requestedIds = new HashSet<int>();
        var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in layerList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                requestedIds.Add(layerId);
            }
            else
            {
                requestedNames.Add(token);
            }
        }

        return accessibleLayers
            .Where(l => requestedIds.Contains(l.Id) ||
                        (!string.IsNullOrWhiteSpace(l.Name) && requestedNames.Contains(l.Name)))
            .ToArray();
    }

    private static int? TryParseWmsCrs(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            return null;
        }

        // Handle EPSG:XXXX and CRS:84
        var trimmed = crs.Trim();
        if (string.Equals(trimmed, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed["EPSG:".Length..];
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
            {
                return srid;
            }
        }

        return TryParseSrid(trimmed);
    }

    private static string BuildWmsCapabilities(ServiceDefinition service, string serviceId, string baseUrl)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<WMS_Capabilities xmlns=\"http://www.opengis.net/wms\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" version=\"1.3.0\">");

        // Service
        sb.AppendLine("  <Service>");
        sb.AppendLine("    <Name>WMS</Name>");
        sb.Append("    <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        if (!string.IsNullOrWhiteSpace(service.Description))
        {
            sb.Append("    <Abstract>").Append(EscapeXml(service.Description)).AppendLine("</Abstract>");
        }

        sb.AppendLine("  </Service>");

        // Capability
        var wmsUrl = $"{baseUrl}/rest/services/{serviceId}/MapServer/WMS";
        sb.AppendLine("  <Capability>");
        sb.AppendLine("    <Request>");

        // GetCapabilities
        sb.AppendLine("      <GetCapabilities>");
        sb.AppendLine("        <Format>application/xml</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get><OnlineResource xlink:href=\"").Append(EscapeXml(wmsUrl)).AppendLine("\" /></Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetCapabilities>");

        // GetMap
        sb.AppendLine("      <GetMap>");
        sb.AppendLine("        <Format>image/png</Format>");
        sb.AppendLine("        <Format>image/jpeg</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get><OnlineResource xlink:href=\"").Append(EscapeXml(wmsUrl)).AppendLine("\" /></Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetMap>");

        sb.AppendLine("    </Request>");

        // Layers
        sb.AppendLine("    <Layer>");
        sb.Append("      <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        sb.AppendLine("      <CRS>EPSG:4326</CRS>");
        sb.AppendLine("      <CRS>EPSG:3857</CRS>");
        sb.AppendLine("      <CRS>CRS:84</CRS>");

        if (service.EffectiveExtent.HasValue)
        {
            var ext = service.EffectiveExtent.Value;
            sb.Append("      <EX_GeographicBoundingBox>");
            sb.Append("<westBoundLongitude>").Append(ext.MinX.ToString("F6", CultureInfo.InvariantCulture)).Append("</westBoundLongitude>");
            sb.Append("<eastBoundLongitude>").Append(ext.MaxX.ToString("F6", CultureInfo.InvariantCulture)).Append("</eastBoundLongitude>");
            sb.Append("<southBoundLatitude>").Append(ext.MinY.ToString("F6", CultureInfo.InvariantCulture)).Append("</southBoundLatitude>");
            sb.Append("<northBoundLatitude>").Append(ext.MaxY.ToString("F6", CultureInfo.InvariantCulture)).Append("</northBoundLatitude>");
            sb.AppendLine("</EX_GeographicBoundingBox>");
        }

        var visibleLayers = service.Layers.Where(l => l.HasGeometry).ToArray();
        foreach (var layer in visibleLayers)
        {
            sb.AppendLine("      <Layer queryable=\"1\">");
            sb.Append("        <Name>").Append(layer.Id.ToString(CultureInfo.InvariantCulture)).AppendLine("</Name>");
            sb.Append("        <Title>").Append(EscapeXml(layer.Name ?? "")).AppendLine("</Title>");
            sb.AppendLine("        <CRS>EPSG:4326</CRS>");
            sb.AppendLine("        <CRS>EPSG:3857</CRS>");

            if (layer.Extent.HasValue)
            {
                var ext = layer.Extent.Value;
                sb.Append("        <EX_GeographicBoundingBox>");
                sb.Append("<westBoundLongitude>").Append(ext.MinX.ToString("F6", CultureInfo.InvariantCulture)).Append("</westBoundLongitude>");
                sb.Append("<eastBoundLongitude>").Append(ext.MaxX.ToString("F6", CultureInfo.InvariantCulture)).Append("</eastBoundLongitude>");
                sb.Append("<southBoundLatitude>").Append(ext.MinY.ToString("F6", CultureInfo.InvariantCulture)).Append("</southBoundLatitude>");
                sb.Append("<northBoundLatitude>").Append(ext.MaxY.ToString("F6", CultureInfo.InvariantCulture)).Append("</northBoundLatitude>");
                sb.AppendLine("</EX_GeographicBoundingBox>");
            }

            sb.AppendLine("      </Layer>");
        }

        sb.AppendLine("    </Layer>");
        sb.AppendLine("  </Capability>");
        sb.AppendLine("</WMS_Capabilities>");

        return sb.ToString();
    }
}
