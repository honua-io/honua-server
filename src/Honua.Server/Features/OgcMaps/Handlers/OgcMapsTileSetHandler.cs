// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OgcMaps.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.OgcMaps.Handlers;

/// <summary>
/// Handler for OGC API - Maps TileSet operations.
/// Provides map tile set metadata for integration with OGC API - Tiles.
/// </summary>
internal sealed class OgcMapsTileSetHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly ILogger<OgcMapsTileSetHandler> _logger;

    public OgcMapsTileSetHandler(
        ILayerCatalog layerCatalog,
        ILogger<OgcMapsTileSetHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets available map tile sets for a collection.
    /// </summary>
    public async Task<IResult> GetMapTileSetsAsync(
        int layerId,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-map-tile-sets");

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var service = await ResolvePrimaryServiceAsync(layerId, cancellationToken);
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            var relativeTilesBasePath = $"/ogc/maps/collections/{layerId}/map/tiles";
            var tilesBasePath = relativeTilesBasePath;
            if (context is not null &&
                BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var configuredBaseUrl) &&
                !string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                tilesBasePath = $"{configuredBaseUrl}{relativeTilesBasePath}";
            }
            var tileMatrixSetBasePath = context is not null && BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var baseUrl) &&
                !string.IsNullOrWhiteSpace(baseUrl)
                ? $"{baseUrl}/ogc/tiles/tileMatrixSets"
                : "/ogc/tiles/tileMatrixSets";

            // Create tile set definitions for common tile matrix sets
            var tileSets = new[]
            {
                // Web Mercator tile set
                new TileSet
                {
                    Title = $"Map tiles for {layer.Name} in Web Mercator",
                    Description = $"Map tiles generated from {layer.Name} using Web Mercator projection",
                    Crs = "http://www.opengis.net/def/crs/EPSG/0/3857",
                    TileMatrixSetId = "WebMercatorQuad",
                    TileMatrixSetUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
                    Links = [
                        new OgcLink
                        {
                            Href = tilesBasePath,
                            Rel = "self",
                            Type = "application/json",
                            Title = "This tileset list"
                        },
                        new OgcLink
                        {
                            Href = $"{tileMatrixSetBasePath}/WebMercatorQuad",
                            Rel = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme",
                            Type = "application/json",
                            Title = "Web Mercator tile matrix set definition"
                        }
                    ]
                },

                // WGS84 tile set
                new TileSet
                {
                    Title = $"Map tiles for {layer.Name} in WGS84",
                    Description = $"Map tiles generated from {layer.Name} using WGS84 projection",
                    Crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                    TileMatrixSetId = "WorldCRS84Quad",
                    TileMatrixSetUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WorldCRS84Quad",
                    Links = [
                        new OgcLink
                        {
                            Href = tilesBasePath,
                            Rel = "self",
                            Type = "application/json",
                            Title = "This tileset list"
                        },
                        new OgcLink
                        {
                            Href = $"{tileMatrixSetBasePath}/WorldCRS84Quad",
                            Rel = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme",
                            Type = "application/json",
                            Title = "WGS84 tile matrix set definition"
                        }
                    ]
                }
            };

            OgcMapsLog.TileSetsRetrieved(_logger, layerId, tileSets.Length);
            scope.SetSuccess(tileSets.Length);

            return Results.Ok(tileSets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.TileSetRetrievalFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while retrieving map tile sets.");
        }
    }

    private static IResult CreateNotFoundResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateNotFound(context, message)
            : Results.NotFound();

    private static IResult CreateErrorResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateInternalServerError(context, message)
            : Results.Problem(message, statusCode: 500);

    private async Task<ServiceDefinition?> ResolvePrimaryServiceAsync(int layerId, CancellationToken cancellationToken)
    {
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services);
        return primaryServices.TryGetValue(layerId, out var service) ? service : null;
    }
}
