// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.MapServer.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int TileSize = 256;
    private const int TileSrid = 3857;
    private const string RequestedTileLayerIdContextItemKey = "__HonuaRequestedTileLayerId";

    /// <summary>
    /// Handle MapServer tile requests for cached raster PNG tiles.
    /// </summary>
    private static async Task<IResult> HandleTile(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        if (!TryParseTileCoordinates(context, out var z, out var y, out var x))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid tile coordinates.");
        }

        var tileLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (z < tileLimits.MinTileZoom || z > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Zoom level {z} is outside supported range ({tileLimits.MinTileZoom}-{tileLimits.MaxTileZoom}).");
        }

        if (!TileMath.ValidateTileCoordinates(x, y, z))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Tile coordinates are out of range.");
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
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

            MapServerLog.TileRequested(logger, serviceId, z, y, x);
            var stopwatch = Stopwatch.StartNew();
            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.MapServerExport, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "tile");
            activity?.SetTag(HonuaTelemetry.Tags.TileZ, z);
            activity?.SetTag(HonuaTelemetry.Tags.TileY, y);
            activity?.SetTag(HonuaTelemetry.Tags.TileX, x);

            var tileBounds = TileMath.GetTileBounds(x, y, z);
            var renderExtent = new SkiaMapRenderer.RenderExtent(
                tileBounds.XMin, tileBounds.YMin, tileBounds.XMax, tileBounds.YMax);
            await using var renderLease = await context.RequestServices
                .GetRequiredService<RasterRenderCapacityLimiter>()
                .TryAcquireAsync(TileSize, TileSize, context.RequestAborted)
                .ConfigureAwait(false);
            if (renderLease is null)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    RasterRenderCapacityLimiter.CapacityExceededMessage,
                    RasterRenderCapacityLimiter.RetryAfterSeconds);
            }

            var renderLayers = ResolveTileLayers(service, context);
            if (renderLayers.Length == 0)
            {
                using var renderer = new SkiaMapRenderer();
                var emptyImage = renderer.RenderMap(
                    [],
                    [],
                    renderExtent,
                    TileSize,
                    TileSize,
                    true,
                    null,
                    GeometryType.None);

                stopwatch.Stop();
                MapServerLog.TileCompleted(logger, serviceId, 0, stopwatch.Elapsed.TotalMilliseconds);
                HonuaTelemetry.SetSuccess(activity, 0);

                return Results.Bytes(emptyImage, "image/png");
            }

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

            var spatialFilter = CreateBboxSpatialFilter(renderExtent, TileSrid);
            var totalFeatureCount = 0;
            var mapConfig = service.Metadata?.MapServer;
            var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;

            using var surface = SKSurface.Create(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface.");
            }

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var transform = SkiaMapRenderer.BuildTransform(renderExtent, TileSize, TileSize);

            foreach (var layer in renderLayers)
            {
                context.RequestAborted.ThrowIfCancellationRequested();

                if (!layer.HasGeometry)
                {
                    continue;
                }

                var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
                var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);
                var featureQuery = CreateRasterFeatureQuery(
                    styleLayers,
                    spatialFilter,
                    service.SpatialReference.Srid,
                    TileSrid,
                    maxFeatures);

                var features = await QueryRasterFeaturesAsync(featureReader, layer.Id, featureQuery, context.RequestAborted);
                if (features.Length == 0)
                {
                    continue;
                }

                totalFeatureCount += features.Length;
                RenderLayerToCanvas(canvas, features, styleLayers, transform, layer.GeometryType);
            }

            var imageBytes = SkiaMapRenderer.EncodeSurface(surface, "png");

            stopwatch.Stop();
            MapServerLog.TileCompleted(logger, serviceId, totalFeatureCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Bytes(imageBytes, "image/png");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.TileFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer tile request failed.");
        }
    }

    private static bool TryParseTileCoordinates(HttpContext context, out int z, out int y, out int x)
    {
        z = 0;
        y = 0;
        x = 0;

        var zValue = context.GetRouteValue("z")?.ToString();
        var yValue = context.GetRouteValue("y")?.ToString();
        var xValue = context.GetRouteValue("x")?.ToString();

        return !string.IsNullOrWhiteSpace(zValue) &&
               int.TryParse(zValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out z) &&
               !string.IsNullOrWhiteSpace(yValue) &&
               int.TryParse(yValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
               !string.IsNullOrWhiteSpace(xValue) &&
               int.TryParse(xValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
    }

    private static LayerDefinition[] ResolveTileLayers(ServiceDefinition service, HttpContext context)
    {
        if (context.Items.TryGetValue(RequestedTileLayerIdContextItemKey, out var requestedLayerIdValue) &&
            requestedLayerIdValue is int requestedLayerId)
        {
            var requestedLayer = service.Layers.FirstOrDefault(layer => layer.Id == requestedLayerId);
            if (requestedLayer is null || !requestedLayer.HasGeometry)
            {
                return [];
            }

            return AccessPolicyHelpers.IsLayerAccessible(context, requestedLayer, service)
                ? [requestedLayer]
                : [];
        }

        return ResolveVisibleLayers(service, null, context);
    }
}
