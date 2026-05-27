// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
    private sealed record TileLayerDescriptor(
        int PublicLayerId,
        int StorageLayerId,
        MetadataV2Resource Resource);

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
            var publishedLayers = ResolveTileLayerDescriptors(snapshot, service);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publishedLayers.Select(static layer => layer.Resource),
                service);
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

            var renderLayers = publishedLayers
                .Where(layer => IsTileLayerVisibleByDefault(layer.Resource))
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .Select(BuildTileRenderDescriptor)
                .ToArray();
            var maxFeatures = service.Settings?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;
            var serviceSrid = ResolveTileServiceSrid(service, publishedLayers);
            var renderResult = await RenderRasterTileV2Async(
                context,
                serviceSrid,
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

    private static TileLayerDescriptor[] ResolveTileLayerDescriptors(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<TileLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int publicLayerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? publicLayerId;
            descriptors.Add(new TileLayerDescriptor(publicLayerId, storageLayerId, resource));
        }

        return [.. descriptors.OrderBy(static layer => layer.PublicLayerId)];
    }

    private static RenderLayerDescriptor BuildTileRenderDescriptor(TileLayerDescriptor layer)
    {
        var geometryType = layer.Resource.ReadGeometryType();
        var hasGeometry = geometryType != MetadataV2GeometryType.None ||
                          layer.Resource.FindPrimaryGeometryField() is not null;
        return CreateRenderLayerDescriptorFromV2(layer.StorageLayerId, hasGeometry, geometryType);
    }

    private static int ResolveTileServiceSrid(
        MetadataV2Service service,
        IReadOnlyList<TileLayerDescriptor> layers)
        => service.SpatialReference?.ResolveSrid()
            ?? layers.Select(static layer => layer.Resource.ReadSrid()).FirstOrDefault(static srid => srid.HasValue)
            ?? 4326;

    private static bool IsTileLayerVisibleByDefault(MetadataV2Resource resource)
        => resource.Display?.DefaultVisibility ?? true;
}
