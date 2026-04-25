// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Server.Features.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer tile requests for cached raster PNG tiles.
    /// </summary>
    private static async Task<IResult> HandleTile(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
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

            var renderLayers = ResolveTileLayers(service, context);
            var mapConfig = service.Metadata?.MapServer;
            var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;
            var renderResult = await RenderRasterTileAsync(
                context,
                service,
                renderLayers,
                z,
                y,
                x,
                maxFeatures,
                cancellationToken).ConfigureAwait(false);
            if (!renderResult.IsSuccess)
            {
                return renderResult.Error!;
            }

            stopwatch.Stop();
            MapServerLog.TileCompleted(logger, serviceId, renderResult.FeatureCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, renderResult.FeatureCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Bytes(renderResult.ImageBytes, "image/png");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        => ResolveVisibleLayers(service, null, context);
}
