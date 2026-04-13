// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
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
    private const string OgcApiMapsProtocol = "OGC-API-Maps";

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

            var service = await ResolvePrimaryServiceAsync(layerId, cancellationToken);
            if (!IsOgcApiMapsEnabled(layer, service))
            {
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            var basePathPrefix = ResolveBasePathPrefix(context);
            var relativeTilesListPath = $"/ogc/maps/collections/{layerId}/map/tiles";
            var tilesListPath = BuildPath(basePathPrefix, relativeTilesListPath);
            var tileMatrixSetBasePath = BuildPath(basePathPrefix, "/ogc/tiles/tileMatrixSets");
            var tileSets = BuildTileSets(layerId, layer.Name, basePathPrefix, tileMatrixSetBasePath);

            var response = new TileSetsList
            {
                Tilesets = tileSets,
                Links =
                [
                    new OgcLink
                    {
                        Href = tilesListPath,
                        Rel = "self",
                        Type = MediaTypes.Json,
                        Title = "This tileset list"
                    },
                    new OgcLink
                    {
                        Href = BuildPath(basePathPrefix, $"/ogc/maps/collections/{layerId}/map"),
                        Rel = "parent",
                        Type = MediaTypes.Png,
                        Title = "Collection map"
                    }
                ]
            };

            OgcMapsLog.TileSetsRetrieved(_logger, layerId, response.Tilesets.Length);
            scope.SetSuccess(response.Tilesets.Length);

            return Results.Ok(response);
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

    /// <summary>
    /// Gets map tile set metadata for a single tile matrix set.
    /// </summary>
    public async Task<IResult> GetMapTileSetAsync(
        int layerId,
        string tileMatrixSetId,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-map-tile-set")
             .WithTag("honua.tile_matrix_set_id", tileMatrixSetId);

        try
        {
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            var service = await ResolvePrimaryServiceAsync(layerId, cancellationToken);
            if (!IsOgcApiMapsEnabled(layer, service))
            {
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            var basePathPrefix = ResolveBasePathPrefix(context);
            var tileMatrixSetBasePath = BuildPath(basePathPrefix, "/ogc/tiles/tileMatrixSets");
            var tileSet = BuildTileSets(layerId, layer.Name, basePathPrefix, tileMatrixSetBasePath)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.TileMatrixSetId,
                    tileMatrixSetId,
                    StringComparison.OrdinalIgnoreCase));

            if (tileSet == null)
            {
                return CreateNotFoundResult(context, $"Tile matrix set '{tileMatrixSetId}' not found");
            }

            scope.SetSuccess(1);
            return Results.Ok(tileSet);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.TileSetRetrievalFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while retrieving map tile set metadata.");
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

    private static TileSet[] BuildTileSets(
        int layerId,
        string? layerName,
        string basePathPrefix,
        string tileMatrixSetBasePath)
    {
        var displayName = string.IsNullOrWhiteSpace(layerName)
            ? $"Layer {layerId}"
            : layerName;

        return OgcTileMatrixSetDescriptors.Supported
            .Select(descriptor => BuildTileSet(
                layerId,
                displayName,
                basePathPrefix,
                tileMatrixSetBasePath,
                descriptor))
            .ToArray();
    }

    private static TileSet BuildTileSet(
        int layerId,
        string displayName,
        string basePathPrefix,
        string tileMatrixSetBasePath,
        OgcTileMatrixSetDescriptor descriptor)
    {
        var tileSetPath = BuildPath(
            basePathPrefix,
            $"/ogc/maps/collections/{layerId}/map/tiles/{descriptor.Id}");
        var tileTemplate = BuildPath(
            basePathPrefix,
            $"/ogc/tiles/collections/{layerId}/tiles/{descriptor.Id}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?f=png");

        return new TileSet
        {
            Title = $"Map tiles for {displayName} in {descriptor.ProjectionName}",
            Description = $"Map tiles generated from {displayName} using {descriptor.ProjectionName} projection",
            Crs = descriptor.Crs,
            TileMatrixSetId = descriptor.Id,
            TileMatrixSetUri = descriptor.Uri,
            Links =
            [
                new OgcLink
                {
                    Href = tileSetPath,
                    Rel = "self",
                    Type = MediaTypes.Json,
                    Title = "This tileset"
                },
                new OgcLink
                {
                    Href = tileTemplate,
                    Rel = "item",
                    Type = MediaTypes.Png,
                    Title = "PNG map tiles",
                    Templated = true
                },
                new OgcLink
                {
                    Href = $"{tileMatrixSetBasePath}/{descriptor.Id}",
                    Rel = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme",
                    Type = MediaTypes.Json,
                    Title = $"{descriptor.ProjectionName} tile matrix set definition"
                }
            ]
        };
    }

    private static string ResolveBasePathPrefix(HttpContext? context)
    {
        if (context is not null &&
            BaseUrlResolver.TryGetConfiguredBaseUrl(context, out var configuredBaseUrl) &&
            !string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return configuredBaseUrl;
        }

        return string.Empty;
    }

    private static string BuildPath(string basePathPrefix, string relativePath)
        => string.IsNullOrWhiteSpace(basePathPrefix)
            ? relativePath
            : $"{basePathPrefix}{relativePath}";

    private static bool IsOgcApiMapsEnabled(LayerDefinition layer, ServiceDefinition? service)
    {
        var metadata = service?.Metadata ?? layer.Metadata;
        return ServiceProtocols.IsProtocolEnabled(metadata, OgcApiMapsProtocol);
    }

    private async Task<ServiceDefinition?> ResolvePrimaryServiceAsync(int layerId, CancellationToken cancellationToken)
    {
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, OgcApiMapsProtocol);
        return primaryServices.TryGetValue(layerId, out var service) ? service : null;
    }
}
