// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.Tiles;
using Honua.Server.Features.OgcTiles.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OgcTiles;

internal static class TilesEndpoints
{
    public static IEndpointRouteBuilder MapTilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var datasetTilesets = endpoints.MapGet("/ogc/tiles/tiles", HandleGetDatasetTilesets)
            .WithDisplayName("OGC API Tiles Tilesets List")
            .WithName("OgcTilesTilesets")
            .WithSummary("Get available tilesets for the dataset")
            .WithDescription("Lists available tilesets for the dataset")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTilesets")
            .Produces<TileSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var datasetTileset = endpoints.MapGet("/ogc/tiles/tiles/{tileMatrixSetId}", HandleGetDatasetTileset)
            .WithDisplayName("OGC API Tiles Dataset Tileset")
            .WithName("OgcTilesDatasetTileset")
            .WithSummary("Get tileset metadata for the dataset")
            .WithDescription("Returns tileset metadata for the dataset and tile matrix set")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTileset")
            .Produces<TileSet>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var datasetTileItem = endpoints.MapGet("/ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetDatasetTileItem)
            .WithDisplayName("OGC API Tiles Dataset Tile")
            .WithName("OgcTilesDatasetTile")
            .WithSummary("Get a tile for the dataset")
            .WithDescription("Returns a Mapbox Vector Tile or PNG tile for the dataset and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTile");

        var collectionTilesets = endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles", HandleGetCollectionTilesets)
            .WithDisplayName("OGC API Tiles Collection Tilesets List")
            .WithName("OgcTilesCollectionTilesets")
            .WithSummary("Get available tilesets for a collection")
            .WithDescription("Lists available tilesets for the specified collection")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollectionTilesets")
            .Produces<TileSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var collectionTileset = endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}", HandleGetCollectionTileset)
            .WithDisplayName("OGC API Tiles Collection Tileset")
            .WithName("OgcTilesCollectionTileset")
            .WithSummary("Get tileset metadata for a collection")
            .WithDescription("Returns tileset metadata for the specified collection and tile matrix set")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollectionTileset")
            .Produces<TileSet>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var collectionTile = endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetCollectionTile)
            .WithDisplayName("OGC API Tiles Collection Tile")
            .WithName("OgcTilesCollectionTile")
            .WithSummary("Get a tile for a collection")
            .WithDescription("Returns a Mapbox Vector Tile or PNG tile for the specified collection and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTile");

        return endpoints;
    }

    private static async Task<IResult> HandleGetDatasetTilesets(
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var collections = OgcCommonUtilities.GetQueryValue(request, "collections");
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.DatasetTilesetMetadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out _))
        {
            return CreateFormatError(context, f);
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (selectedLayers, layerError) = await ResolveDatasetLayersAsync(collections, layerCatalog, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var titleBase = BuildDatasetTitleBase(selectedLayers!);
        var querySuffix = BuildCollectionsQuerySuffix(collections, selectedLayers!);
        var tilesets = BuildDatasetTileSetItems(titleBase, baseUrl, tileLimits, querySuffix).ToImmutableArray();
        return BuildTilesetsListResponse(
            request,
            $"{baseUrl}/ogc/tiles/tiles{querySuffix}",
            $"{baseUrl}/ogc/tiles",
            "Landing page",
            tilesets,
            outputFormat);
    }

    private static async Task<IResult> HandleGetDatasetTileset(
        string tileMatrixSetId,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var collections = OgcCommonUtilities.GetQueryValue(request, "collections");
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.DatasetTilesetMetadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out _))
        {
            return CreateFormatError(context, f);
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layers, layerError) = await ResolveDatasetLayersAsync(collections, layerCatalog, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var querySuffix = BuildCollectionsQuerySuffix(collections, layers!);
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{tileMatrixSetId}{querySuffix}";
        var tileTemplate = $"{baseUrl}/ogc/tiles/tiles/{tileMatrixSetId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}{querySuffix}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{tileMatrixSetId}";
        var geodataHref = layers!.Length == 1
            ? $"{baseUrl}/ogc/features/collections/{layers[0].Id}"
            : $"{baseUrl}/ogc/features/collections";
        var geodataTitle = layers.Length == 1
            ? "Collection metadata"
            : "Collections metadata";
        var titleBase = BuildDatasetTitleBase(layers);
        var description = layers.Length == 1
            ? layers[0].Description
            : $"Dataset tiles across {layers.Length} collections.";

        var tileset = BuildTileset(
            titleBase,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            geodataHref,
            geodataTitle,
            description,
            tileLimits,
            tileMatrixSetId);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetDatasetTileItem(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] ITileProvider tileProvider,
        [FromServices] IOptions<TileOptions> tileOptions,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var datetime = OgcCommonUtilities.GetQueryValue(request, "datetime");
        var subset = OgcCommonUtilities.GetQueryValue(request, "subset");
        var crs = OgcCommonUtilities.GetQueryValue(request, "crs");
        var subsetCrs = OgcCommonUtilities.GetQueryValue(request, "subset-crs");
        var collections = OgcCommonUtilities.GetQueryValue(request, "collections");

        return await HandleTileRequestAsync(
            tileMatrixSetId,
            tileMatrix,
            tileRow,
            tileCol,
            context,
            f,
            datetime,
            subset,
            crs,
            subsetCrs,
            OgcTilesUtilities.AllowedQueryParameters.DatasetTiles,
            cancellationToken => ResolveDatasetLayersAsync(collections, layerCatalog, context, cancellationToken),
            layer => layer.SpatialReference.Wkid,
            tileProvider,
            tileOptions,
            limitsOptions);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out _))
        {
            return CreateFormatError(context, f);
        }

        if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var services = await layerCatalog.ListServicesAsync(cancellationToken);
        var primaryService = GetPrimaryService(layer.Id, LayerValidationHelpers.BuildPrimaryServiceMap(services));
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, primaryService);
        if (accessError != null)
        {
            return accessError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var tilesets = BuildTileSetItems(layerId, layer.Name, baseUrl, tileLimits).ToImmutableArray();
        return BuildTilesetsListResponse(
            request,
            $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles",
            $"{baseUrl}/ogc/tiles/collections/{collectionId}",
            "Collection",
            tilesets,
            outputFormat);
    }

    private static async Task<IResult> HandleGetCollectionTileset(
        string collectionId,
        string tileMatrixSetId,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out _))
        {
            return CreateFormatError(context, f);
        }

        if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
        if (accessError != null)
        {
            return accessError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{tileMatrixSetId}";
        var titleBase = string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {layerId}" : layer.Name;

        var tileset = BuildTileset(
            titleBase,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            $"{baseUrl}/ogc/features/collections/{collectionId}",
            "Collection metadata",
            layer.Description,
            tileLimits,
            tileMatrixSetId);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetCollectionTile(
        string collectionId,
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] ITileProvider tileProvider,
        [FromServices] IOptions<TileOptions> tileOptions,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var datetime = OgcCommonUtilities.GetQueryValue(request, "datetime");
        var subset = OgcCommonUtilities.GetQueryValue(request, "subset");
        var crs = OgcCommonUtilities.GetQueryValue(request, "crs");
        var subsetCrs = OgcCommonUtilities.GetQueryValue(request, "subset-crs");

        return await HandleTileRequestAsync(
            tileMatrixSetId,
            tileMatrix,
            tileRow,
            tileCol,
            context,
            f,
            datetime,
            subset,
            crs,
            subsetCrs,
            OgcTilesUtilities.AllowedQueryParameters.Tiles,
            async cancellationToken =>
            {
                if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
                {
                    return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
                }

                var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
                if (layer == null)
                {
                    return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
                }

                var services = await layerCatalog.ListServicesAsync(cancellationToken);
                var primaryService = GetPrimaryService(layer.Id, LayerValidationHelpers.BuildPrimaryServiceMap(services));
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, primaryService);
                if (accessError != null)
                {
                    return (Array.Empty<LayerDefinition>(), accessError);
                }

                return (new[] { layer }, null);
            },
            layer => layer.SpatialReference.ToSrid(),
            tileProvider,
            tileOptions,
            limitsOptions);
    }

    private static async Task<IResult> HandleTileRequestAsync(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        string? format,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        IReadOnlySet<string> allowedQueryParameters,
        Func<CancellationToken, Task<(LayerDefinition[] Layers, IResult? Error)>> resolveLayersAsync,
        Func<LayerDefinition, int> getSpatialReferenceSrid,
        ITileProvider tileProvider,
        IOptions<TileOptions> tileOptions,
        IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, allowedQueryParameters);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        var isRaster = OgcTilesUtilities.IsRasterTileFormat(format, request);

        if (!isRaster)
        {
            if (!string.IsNullOrWhiteSpace(format) &&
                !string.Equals(format, "mvt", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateNotAcceptable(context, "Requested tile format is not supported.");
            }

            if (!OgcTilesUtilities.AcceptsVectorTiles(request))
            {
                return StandardErrorHelpers.CreateNotAcceptable(context, "Requested tile format is not acceptable.");
            }
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId);

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var tileOptionsValue = tileOptions.Value;
        var tileLimits = limitsOptions.Value.Tiles;
        if (zoomLevel < tileLimits.MinTileZoom || zoomLevel > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
        }

        var validCoords = isGeographic
            ? TileMath.ValidateTileCoordinatesGeographic(tileCol, tileRow, zoomLevel)
            : TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel);

        if (!validCoords)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layers, layerError) = await resolveLayersAsync(cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        if (layers.Length == 0)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Collection not found.");
        }

        var layer = layers[0];

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcTiles);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, string.Join(",", layers.Select(candidate => candidate.Id)));
        activity?.SetTag("honua.layer_count", layers.Length);
        activity?.SetTag(HonuaTelemetry.Tags.TileZ, zoomLevel);
        activity?.SetTag(HonuaTelemetry.Tags.TileX, tileCol);
        activity?.SetTag(HonuaTelemetry.Tags.TileY, tileRow);

        var validationResult = ValidateTileQueryParameters(
            context, layer, datetime, subset, crs, subsetCrs, tileMatrixSetId, out var temporalFilter);
        if (validationResult is not null)
        {
            return validationResult;
        }

        // Raster (PNG) tile path
        if (isRaster)
        {
            return layers.Length == 1
                ? await HandleRasterTileAsync(
                    context, layer, tileCol, tileRow, zoomLevel, isGeographic,
                    getSpatialReferenceSrid, temporalFilter, tileLimits,
                    tileOptionsValue, activity, cancellationToken)
                : await HandleDatasetRasterTileAsync(
                    context,
                    layers,
                    tileCol,
                    tileRow,
                    zoomLevel,
                    isGeographic,
                    getSpatialReferenceSrid,
                    temporalFilter,
                    tileLimits,
                    tileOptionsValue,
                    activity,
                    cancellationToken);
        }

        return layers.Length == 1
            ? await VectorTileExecution.ExecuteAsync(
                context,
                tileProvider,
                layer,
                tileCol,
                tileRow,
                zoomLevel,
                VectorTileExecution.CreateQuery(
                    getSpatialReferenceSrid(layer),
                    temporalFilter: temporalFilter),
                tileOptionsValue,
                tileLimits,
                cancellationToken,
                activity)
            : await ExecuteDatasetVectorTileAsync(
                context,
                tileProvider,
                layers,
                tileCol,
                tileRow,
                zoomLevel,
                getSpatialReferenceSrid,
                temporalFilter,
                tileOptionsValue,
                tileLimits,
                activity,
                cancellationToken);
    }

    private static async Task<IResult> HandleRasterTileAsync(
        HttpContext context,
        LayerDefinition layer,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
        Func<LayerDefinition, int> getSpatialReferenceSrid,
        TemporalFilter? temporalFilter,
        TileLimits tileLimits,
        TileOptions tileOptionsValue,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var bounds = isGeographic
            ? TileMath.GetTileBoundsGeographic(tileCol, tileRow, zoomLevel)
            : TileMath.GetTileBounds(tileCol, tileRow, zoomLevel);

        var filterSrid = isGeographic ? 4326 : 3857;
        var spatialFilter = CreateBboxSpatialFilter(bounds, filterSrid);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var featureQuery = new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = getSpatialReferenceSrid(layer),
            OutputSrid = filterSrid,
            Limit = tileLimits.MaxFeaturesPerTile > 0 ? tileLimits.MaxFeaturesPerTile : 10_000,
            TemporalFilter = temporalFilter
        };

        var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, cancellationToken);

        if (queryResult.Items.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            return Results.NoContent();
        }

        var imageBytes = TileRenderer.RenderTilePng(queryResult.Items, bounds, layer.GeometryType);

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, queryResult.Items.Length);
        activity?.SetTag("honua.tile.bytes", imageBytes.Length);
        activity?.SetTag("honua.tile.format", "png");

        return CreatePngTileResult(imageBytes, tileOptionsValue.CacheMaxAge);
    }

    private static async Task<IResult> ExecuteDatasetVectorTileAsync(
        HttpContext context,
        ITileProvider tileProvider,
        LayerDefinition[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        Func<LayerDefinition, int> getSpatialReferenceSrid,
        TemporalFilter? temporalFilter,
        TileOptions tileOptionsValue,
        TileLimits tileLimits,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var mergedTile = await MergeVectorTileAsync(
            tileProvider,
            layers,
            tileCol,
            tileRow,
            zoomLevel,
            getSpatialReferenceSrid,
            temporalFilter,
            tileOptionsValue,
            tileLimits,
            cancellationToken);

        if (mergedTile == null || mergedTile.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", mergedTile.Length);
        context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptionsValue.CacheMaxAge}";
        return Results.Bytes(mergedTile, MediaTypes.Mvt);
    }

    private static async Task<IResult> HandleDatasetRasterTileAsync(
        HttpContext context,
        LayerDefinition[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
        Func<LayerDefinition, int> getSpatialReferenceSrid,
        TemporalFilter? temporalFilter,
        TileLimits tileLimits,
        TileOptions tileOptionsValue,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var bounds = isGeographic
            ? TileMath.GetTileBoundsGeographic(tileCol, tileRow, zoomLevel)
            : TileMath.GetTileBounds(tileCol, tileRow, zoomLevel);

        var filterSrid = isGeographic ? 4326 : 3857;
        var spatialFilter = CreateBboxSpatialFilter(bounds, filterSrid);
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var renderedLayers = new List<TileRenderer.TileRenderLayer>(layers.Length);
        var remainingBudget = tileLimits.MaxFeaturesPerTile > 0 ? tileLimits.MaxFeaturesPerTile : 10_000;
        var totalFeatureCount = 0;

        foreach (var layer in layers)
        {
            if (remainingBudget <= 0)
            {
                break;
            }

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = getSpatialReferenceSrid(layer),
                OutputSrid = filterSrid,
                Limit = remainingBudget,
                TemporalFilter = temporalFilter
            };

            var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, cancellationToken);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            renderedLayers.Add(new TileRenderer.TileRenderLayer(queryResult.Items, layer.GeometryType));
            totalFeatureCount += queryResult.Items.Length;
            remainingBudget -= queryResult.Items.Length;
        }

        if (renderedLayers.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            return Results.NoContent();
        }

        var imageBytes = TileRenderer.RenderTilePng(renderedLayers, bounds);

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, totalFeatureCount);
        activity?.SetTag("honua.tile.bytes", imageBytes.Length);
        activity?.SetTag("honua.tile.format", "png");

        return CreatePngTileResult(imageBytes, tileOptionsValue.CacheMaxAge);
    }

    private static async Task<byte[]?> MergeVectorTileAsync(
        ITileProvider tileProvider,
        LayerDefinition[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        Func<LayerDefinition, int> getSpatialReferenceSrid,
        TemporalFilter? temporalFilter,
        TileOptions tileOptionsValue,
        TileLimits tileLimits,
        CancellationToken cancellationToken)
    {
        List<byte[]>? tileParts = null;
        var totalLength = 0;

        foreach (var layer in layers)
        {
            var query = VectorTileExecution.CreateQuery(
                getSpatialReferenceSrid(layer),
                temporalFilter: temporalFilter);
            var tileData = await tileProvider.GetMvtTileAsync(
                layer.Id,
                tileCol,
                tileRow,
                zoomLevel,
                query,
                tileOptionsValue,
                tileLimits,
                cancellationToken);

            if (tileData == null || tileData.Length == 0)
            {
                continue;
            }

            tileParts ??= new List<byte[]>(layers.Length);
            tileParts.Add(tileData);
            totalLength += tileData.Length;
        }

        if (tileParts is null || tileParts.Count == 0)
        {
            return null;
        }

        if (tileParts.Count == 1)
        {
            return tileParts[0];
        }

        var merged = new byte[totalLength];
        var offset = 0;
        foreach (var tilePart in tileParts)
        {
            Buffer.BlockCopy(tilePart, 0, merged, offset, tilePart.Length);
            offset += tilePart.Length;
        }

        return merged;
    }

    private static SpatialFilter CreateBboxSpatialFilter(TileBounds bounds, int srid)
        => SpatialFilterHelpers.CreateBboxSpatialFilter(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax, srid);

    private static IEnumerable<TileSetItem> BuildDatasetTileSetItems(string titleBase, string baseUrl, TileLimits tileLimits, string querySuffix)
    {
        // WebMercatorQuad
        yield return BuildTileSetItemCore(
            $"{titleBase} ({OgcTilesUtilities.WebMercatorQuadId})",
            $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}{querySuffix}",
            $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}{querySuffix}",
            $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}",
            "Dataset tileset metadata",
            tileLimits,
            OgcTilesUtilities.WebMercatorQuadId);

        // WorldCRS84Quad
        yield return BuildTileSetItemCore(
            $"{titleBase} ({OgcTilesUtilities.WorldCrs84QuadId})",
            $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WorldCrs84QuadId}{querySuffix}",
            $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WorldCrs84QuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}{querySuffix}",
            $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WorldCrs84QuadId}",
            "Dataset tileset metadata",
            tileLimits,
            OgcTilesUtilities.WorldCrs84QuadId);
    }

    private static IEnumerable<TileSetItem> BuildTileSetItems(int layerId, string? layerName, string baseUrl, TileLimits tileLimits)
    {
        var collectionId = layerId.ToString(CultureInfo.InvariantCulture);

        // WebMercatorQuad
        var wmHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        yield return BuildTileSetItemCore(
            string.IsNullOrWhiteSpace(layerName) ? $"Layer {layerId}" : $"{layerName} ({OgcTilesUtilities.WebMercatorQuadId})",
            wmHref,
            $"{wmHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}",
            $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}",
            "Tileset metadata",
            tileLimits,
            OgcTilesUtilities.WebMercatorQuadId);

        // WorldCRS84Quad
        var geoHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WorldCrs84QuadId}";
        yield return BuildTileSetItemCore(
            string.IsNullOrWhiteSpace(layerName) ? $"Layer {layerId}" : $"{layerName} ({OgcTilesUtilities.WorldCrs84QuadId})",
            geoHref,
            $"{geoHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}",
            $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WorldCrs84QuadId}",
            "Tileset metadata",
            tileLimits,
            OgcTilesUtilities.WorldCrs84QuadId);
    }

    private static TileSetItem BuildTileSetItemCore(
        string title,
        string tilesetHref,
        string tileTemplate,
        string tileMatrixSetHref,
        string selfLinkTitle,
        TileLimits tileLimits,
        string tileMatrixSetId)
    {
        var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId);
        var crs = isGeographic ? OgcTilesUtilities.Crs84 : OgcTilesUtilities.WebMercatorCrs;
        var uri = isGeographic ? OgcTilesUtilities.WorldCrs84QuadUri : OgcTilesUtilities.WebMercatorQuadUri;
        var matrixLimits = isGeographic
            ? OgcTilesUtilities.BuildWorldCrs84QuadLimits(tileLimits)
            : BuildTileMatrixSetLimits(tileLimits);

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: selfLinkTitle),
            new Link
            {
                Href = tileTemplate,
                Rel = "item",
                Type = MediaTypes.Mvt,
                Title = "Vector tiles",
                Templated = true
            },
            new Link
            {
                Href = tileTemplate,
                Rel = "item",
                Type = MediaTypes.Png,
                Title = "Raster tiles",
                Templated = true
            },
            Link.Create(
                href: tileMatrixSetHref,
                rel: RelationTypes.TilingScheme,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"));

        return new TileSetItem
        {
            Title = title,
            DataType = "map",
            Crs = crs,
            TileMatrixSetId = tileMatrixSetId,
            TileMatrixSetUri = uri,
            TileMatrixSetLimits = matrixLimits,
            Links = links
        };
    }

    private static TileSet BuildTileset(
        string titleBase,
        string tilesetHref,
        string tileTemplate,
        string tileMatrixSetHref,
        string geodataHref,
        string geodataTitle,
        string? description,
        TileLimits tileLimits,
        string tileMatrixSetId)
    {
        var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId);
        var crs = isGeographic ? OgcTilesUtilities.Crs84 : OgcTilesUtilities.WebMercatorCrs;
        var uri = isGeographic ? OgcTilesUtilities.WorldCrs84QuadUri : OgcTilesUtilities.WebMercatorQuadUri;
        var matrixLimits = isGeographic
            ? OgcTilesUtilities.BuildWorldCrs84QuadLimits(tileLimits)
            : BuildTileMatrixSetLimits(tileLimits);

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: $"{titleBase} tileset"),
            new Link
            {
                Href = tileTemplate,
                Rel = "item",
                Type = MediaTypes.Mvt,
                Title = "Vector tiles",
                Templated = true
            },
            new Link
            {
                Href = tileTemplate,
                Rel = "item",
                Type = MediaTypes.Png,
                Title = "Raster tiles",
                Templated = true
            },
            Link.Create(
                href: tileMatrixSetHref,
                rel: RelationTypes.TilingScheme,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"),
            Link.Create(
                href: geodataHref,
                rel: RelationTypes.Geodata,
                type: MediaTypes.Json,
                title: geodataTitle)
        );

        return new TileSet
        {
            Title = $"{titleBase} ({tileMatrixSetId})",
            Description = description,
            DataType = "map",
            Crs = crs,
            TileMatrixSetId = tileMatrixSetId,
            TileMatrixSetUri = uri,
            TileMatrixSetLimits = matrixLimits,
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt, MediaTypes.Png)
        };
    }

    private static IResult BuildTilesetsListResponse(
        HttpRequest request,
        string tilesetsPath,
        string parentHref,
        string parentTitle,
        ImmutableArray<TileSetItem> tilesets,
        string outputFormat)
    {
        var links = OgcCommonUtilities.BuildFormatLinks(
                request,
                tilesetsPath,
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Tilesets")
            .ToBuilder();

        links.Add(Link.Create(
            href: parentHref,
            rel: "parent",
            type: MediaTypes.Json,
            title: parentTitle));

        var response = new TileSetsList
        {
            Tilesets = tilesets,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileSetsList, outputFormat, "Tilesets");
    }

    private static ImmutableArray<TileMatrixSetLimit> BuildTileMatrixSetLimits(TileLimits limits)
        => OgcTilesUtilities.BuildWebMercatorQuadLimits(limits);

    private static async Task<(LayerDefinition[] Layers, IResult? Error)> ResolveDatasetLayersAsync(
        string? collections,
        ILayerCatalog layerCatalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var services = await layerCatalog.ListServicesAsync(cancellationToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services);

        if (string.IsNullOrWhiteSpace(collections))
        {
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            if (layers.Length == 0)
            {
                return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateNotFound(context, "No collections are available."));
            }

            var accessibleLayers = new List<LayerDefinition>(layers.Length);
            var requiresAuth = false;
            var hasDenied = false;

            foreach (var layer in layers)
            {
                var service = GetPrimaryService(layer.Id, primaryServices);
                var decision = AccessPolicyHelpers.EvaluateAccess(
                    context,
                    layer.Metadata?.AccessPolicy,
                    service?.Metadata?.AccessPolicy);

                if (decision.IsAllowed)
                {
                    accessibleLayers.Add(layer);
                    continue;
                }

                hasDenied = true;
                if (decision.RequiresAuthentication)
                {
                    requiresAuth = true;
                }
            }

            var accessibleLayersArray = accessibleLayers
                .OrderBy(layer => layer.Id)
                .ToArray();

            if (accessibleLayersArray.Length == 0)
            {
                if (hasDenied)
                {
                    return (Array.Empty<LayerDefinition>(), requiresAuth
                        ? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage)
                        : StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage));
                }

                return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateNotFound(context, "No collections are available."));
            }

            return (accessibleLayersArray, null);
        }

        if (!TryParseCollectionIds(collections, out var collectionIds, out var parseError))
        {
            return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid collections parameter."));
        }

        var selectedLayers = new List<LayerDefinition>(collectionIds.Length);
        foreach (var collectionId in collectionIds)
        {
            var selectedLayer = await layerCatalog.GetLayerAsync(collectionId, cancellationToken);
            if (selectedLayer == null)
            {
                return (Array.Empty<LayerDefinition>(), StandardErrorHelpers.CreateBadRequest(context, $"Collection '{collectionId}' not found."));
            }

            var primaryService = GetPrimaryService(selectedLayer.Id, primaryServices);
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, selectedLayer, primaryService);
            if (accessError != null)
            {
                return (Array.Empty<LayerDefinition>(), accessError);
            }

            selectedLayers.Add(selectedLayer);
        }

        return (selectedLayers.OrderBy(layer => layer.Id).ToArray(), null);
    }

    private static bool TryParseCollectionIds(string collections, out int[] collectionIds, out string? error)
    {
        collectionIds = Array.Empty<int>();
        error = null;

        if (HasEmptyCommaSeparatedToken(collections))
        {
            error = "Invalid collections parameter.";
            return false;
        }

        var values = collections.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            error = "Invalid collections parameter.";
            return false;
        }

        var parsedIds = new SortedSet<int>();
        foreach (var rawValue in values)
        {
            var value = ExtractCollectionId(rawValue);
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "Invalid collections parameter.";
                return false;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var collectionId))
            {
                error = $"Invalid collections parameter '{collections}'.";
                return false;
            }

            parsedIds.Add(collectionId);
        }

        if (parsedIds.Count == 0)
        {
            error = "Invalid collections parameter.";
            return false;
        }

        collectionIds = parsedIds.ToArray();

        return true;
    }

    private static string BuildDatasetTitleBase(LayerDefinition[] layers)
    {
        if (layers.Length == 1)
        {
            var layer = layers[0];
            return string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {layer.Id}" : layer.Name;
        }

        return "Dataset";
    }

    private static string BuildCollectionsQuerySuffix(string? requestedCollections, LayerDefinition[] layers)
    {
        if (string.IsNullOrWhiteSpace(requestedCollections))
        {
            return string.Empty;
        }

        var canonicalCollections = string.Join(
            ",",
            layers.Select(layer => Uri.EscapeDataString(layer.Id.ToString(CultureInfo.InvariantCulture))));
        return $"?collections={canonicalCollections}";
    }

    private static string? ExtractCollectionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        const string marker = "/collections/";
        var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            trimmed = trimmed[(index + marker.Length)..];
        }

        trimmed = trimmed.TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool HasEmptyCommaSeparatedToken(string value)
    {
        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            if (token.Trim().Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IResult? ValidateTileQueryParameters(
        HttpContext context,
        LayerDefinition layer,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        string tileMatrixSetId,
        out TemporalFilter? temporalFilter)
    {
        temporalFilter = null;

        if (!string.IsNullOrWhiteSpace(subset))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "The subset parameter is not supported.");
        }

        if (!IsSupportedCrs(crs, tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported crs '{crs}'. Only the CRS matching the tile matrix set is supported.");
        }

        if (!IsSupportedCrs(subsetCrs, tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported subset-crs '{subsetCrs}'. Only the CRS matching the tile matrix set is supported.");
        }

        if (!OgcTemporalFilterParser.TryParse(datetime, layer, out temporalFilter, out var errorMessage))
        {
            return StandardErrorHelpers.CreateBadRequest(context, errorMessage ?? "Invalid datetime parameter.");
        }

        return null;
    }

    private static bool IsSupportedCrs(string? crs, string tileMatrixSetId)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            return true;
        }

        var trimmed = crs.Trim();
        var normalized = OgcFeaturesUtilities.NormalizeCrsUri(trimmed);

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epsg))
        {
            normalized = $"http://www.opengis.net/def/crs/EPSG/0/{epsg}";
        }

        if (OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId))
        {
            return string.Equals(normalized, OgcTilesUtilities.Crs84, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "http://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normalized, OgcTilesUtilities.WebMercatorCrs, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateFormatError(HttpContext context, string? formatParameter)
    {
        if (string.IsNullOrWhiteSpace(formatParameter))
        {
            return StandardErrorHelpers.CreateNotAcceptable(context, "Requested format is not acceptable.");
        }

        var normalized = formatParameter.Trim().ToLowerInvariant();
        var detail = normalized switch
        {
            "geojson" => "GeoJSON format is only supported for feature content.",
            "gml" => "GML format is only supported for feature content.",
            "xml" => "GML format is only supported for feature content.",
            _ => $"Unsupported format '{formatParameter}'."
        };

        return StandardErrorHelpers.CreateBadRequest(context, detail);
    }

    private static TileResult CreateTileResult(byte[] tileData, int cacheMaxAge)
        => new TileResult(tileData, cacheMaxAge);

    private static PngTileResult CreatePngTileResult(byte[] imageData, int cacheMaxAge)
        => new PngTileResult(imageData, cacheMaxAge);

    private sealed class TileResult : IResult
    {
        private readonly IResult _inner;
        private readonly int _cacheMaxAge;

        public TileResult(byte[] tileData, int cacheMaxAge)
        {
            _inner = Results.Bytes(tileData, MediaTypes.Mvt);
            _cacheMaxAge = cacheMaxAge;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Cache-Control"] = $"public, max-age={_cacheMaxAge}";
            await _inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class PngTileResult : IResult
    {
        private readonly IResult _inner;
        private readonly int _cacheMaxAge;

        public PngTileResult(byte[] imageData, int cacheMaxAge)
        {
            _inner = Results.Bytes(imageData, MediaTypes.Png);
            _cacheMaxAge = cacheMaxAge;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Cache-Control"] = $"public, max-age={_cacheMaxAge}";
            await _inner.ExecuteAsync(httpContext);
        }
    }

    private static ServiceDefinition? GetPrimaryService(
        int layerId,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
        => primaryServices.TryGetValue(layerId, out var service) ? service : null;

}
