// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
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
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return Results.NotFound();
            }

            // Start telemetry activity
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "metadata",
                HonuaTelemetry.Protocols.OgcMaps,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "get-map-tile-sets");

            // Create tile set definitions for common tile matrix sets
            var tileSets = new[]
            {
                // Web Mercator tile set
                new TileSet
                {
                    Title = $"Map tiles for {layer.Name} in Web Mercator",
                    Description = $"Map tiles generated from {layer.Name} using Web Mercator projection",
                    Crs = "http://www.opengis.net/def/crs/EPSG/0/3857",
                    TileMatrixSetUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
                    Links = [
                        new OgcLink
                        {
                            Href = $"/ogc/maps/collections/{layerId}/map/tiles",
                            Rel = "self",
                            Type = "application/json",
                            Title = "Available tile sets for this collection"
                        }
                    ]
                },

                // WGS84 tile set
                new TileSet
                {
                    Title = $"Map tiles for {layer.Name} in WGS84",
                    Description = $"Map tiles generated from {layer.Name} using WGS84 projection",
                    Crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                    TileMatrixSetUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WorldCRS84Quad",
                    Links = [
                        new OgcLink
                        {
                            Href = $"/ogc/maps/collections/{layerId}/map/tiles",
                            Rel = "self",
                            Type = "application/json",
                            Title = "Available tile sets for this collection"
                        }
                    ]
                }
            };

            OgcMapsLog.TileSetsRetrieved(_logger, layerId, tileSets.Length);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, tileSets.Length);

            return Results.Ok(tileSets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.TileSetRetrievalFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while retrieving map tile sets.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }
}
