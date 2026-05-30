// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Api.Tiles.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static partial class TilesEndpoints
{
    private const string OgcApiTilesProtocol = MetadataV2ServiceProtocols.OgcApiTiles;
    private const int MaxCollectionsPerDatasetRasterTileRequest = 16;
    private static readonly WKBWriter BboxWkbWriter = new();

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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
        var (selectedLayers, layerError) = await ResolveDatasetLayersAsync(collections, graphProvider, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var titleBase = BuildDatasetTitleBase(selectedLayers!);
        var querySuffix = BuildCollectionsQuerySuffix(collections, selectedLayers!);
        var advertiseVectorTiles = selectedLayers!.Length == 1;
        var tilesets = BuildDatasetTileSetItems(titleBase, baseUrl, tileLimits, querySuffix, advertiseVectorTiles).ToImmutableArray();
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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
        var (layers, layerError) = await ResolveDatasetLayersAsync(collections, graphProvider, context, cancellationToken);
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
            ? $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(layers[0].CollectionId)}"
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
            tileMatrixSetId,
            advertiseVectorTiles: layers.Length == 1);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetDatasetTileItem(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
            cancellationToken => ResolveDatasetLayersAsync(collections, graphProvider, context, cancellationToken),
            tileProvider,
            tileOptions,
            limitsOptions);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
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

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolution = ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var layer = resolution.Layer!;
        var tileLimits = limitsOptions.Value.Tiles;
        var encodedCollectionId = Uri.EscapeDataString(collectionId);
        var tilesets = BuildTileSetItems(layer, baseUrl, tileLimits, encodedCollectionId).ToImmutableArray();
        return BuildTilesetsListResponse(
            request,
            $"{baseUrl}/ogc/tiles/collections/{encodedCollectionId}/tiles",
            $"{baseUrl}/ogc/tiles/collections/{encodedCollectionId}",
            "Collection",
            tilesets,
            outputFormat);
    }

    private static async Task<IResult> HandleGetCollectionTileset(
        string collectionId,
        string tileMatrixSetId,
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
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

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolution = ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var layer = resolution.Layer!;
        var tileLimits = limitsOptions.Value.Tiles;
        var encodedCollectionId = Uri.EscapeDataString(collectionId);
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{encodedCollectionId}/tiles/{tileMatrixSetId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{tileMatrixSetId}";

        var tileset = BuildTileset(
            layer.DisplayName,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            $"{baseUrl}/ogc/features/collections/{encodedCollectionId}",
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
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ITileProvider tileProvider,
        [FromServices] IOptions<TileOptions> tileOptions,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        // TODO(#1144): wire tenant filter — resolve ITenantContext from context.RequestServices
        // and reject (or scope) tile requests whose collectionId is not visible to the caller's tenant.
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
                var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                var resolution = ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false);
                if (resolution.Error is not null)
                {
                    return (Array.Empty<TileRequestLayer>(), resolution.Error);
                }

                return (new[] { resolution.Layer! }, null);
            },
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
        Func<CancellationToken, Task<(TileRequestLayer[] Layers, IResult? Error)>> resolveLayersAsync,
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

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.TileGeneration, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcTiles);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, string.Join(",", layers.Select(candidate => candidate.PublicLayerId)));
        activity?.SetTag("honua.layer_count", layers.Length);
        activity?.SetTag(HonuaTelemetry.Tags.TileZ, zoomLevel);
        activity?.SetTag(HonuaTelemetry.Tags.TileX, tileCol);
        activity?.SetTag(HonuaTelemetry.Tags.TileY, tileRow);

        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?
            .CreateLogger(typeof(TilesEndpoints));

        try
        {
            var validationResult = ValidateTileQueryParameters(
                context,
                layers,
                datetime,
                subset,
                crs,
                subsetCrs,
                tileMatrixSetId,
                out var temporalFilters);
            if (validationResult is not null)
            {
                return validationResult;
            }

            if (isRaster && layers.Length > MaxCollectionsPerDatasetRasterTileRequest)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"PNG dataset tiles support at most {MaxCollectionsPerDatasetRasterTileRequest} collections per request. Narrow the request with the collections parameter.");
            }

            var layer = layers[0];

            // Raster (PNG) tile path
            if (isRaster)
            {
                return layers.Length == 1
                    ? await HandleRasterTileAsync(
                        context,
                        layer,
                        tileCol,
                        tileRow,
                        zoomLevel,
                        isGeographic,
                        GetTemporalFilterForLayer(layer, temporalFilters),
                        tileLimits,
                        tileOptionsValue,
                        activity,
                        cancellationToken)
                    : await HandleDatasetRasterTileAsync(
                        context,
                        layers,
                        tileCol,
                        tileRow,
                        zoomLevel,
                        isGeographic,
                        temporalFilters,
                        tileLimits,
                        tileOptionsValue,
                        activity,
                        cancellationToken);
            }

            return layers.Length == 1
                ? await VectorTileExecution.ExecuteAsync(
                    context,
                    tileProvider,
                    layer.StorageLayerId,
                    tileCol,
                    tileRow,
                    zoomLevel,
                    CreateVectorTileQuery(
                        layer.SpatialReferenceSrid,
                        isGeographic,
                        temporalFilter: GetTemporalFilterForLayer(layer, temporalFilters)),
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
                    isGeographic,
                    GetTemporalFilterForLayer(layer, temporalFilters),
                    tileOptionsValue,
                    tileLimits,
                    activity,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            if (logger != null)
            {
                LogTileResponseGenerationFailed(
                    logger,
                    string.Join(",", layers.Select(candidate => candidate.PublicLayerId)),
                    tileMatrixSetId,
                    zoomLevel,
                    tileCol,
                    tileRow,
                    ex);
            }
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while generating the requested tile.");
        }
    }

    private static async Task<IResult> HandleRasterTileAsync(
        HttpContext context,
        TileRequestLayer layer,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
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
            SpatialReferenceSrid = layer.SpatialReferenceSrid,
            OutputSrid = filterSrid,
            Limit = tileLimits.MaxFeaturesPerTile > 0 ? tileLimits.MaxFeaturesPerTile : 10_000,
            TemporalFilter = temporalFilter
        };

        var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);

        if (queryResult.Items.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptionsValue.CacheMaxAge}";
            return Results.NoContent();
        }

        var imageBytes = TileRenderer.RenderTilePng(queryResult.Items, bounds, ToTileGeometryKind(layer.GeometryType));

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, queryResult.Items.Length);
        activity?.SetTag("honua.tile.bytes", imageBytes.Length);
        activity?.SetTag("honua.tile.format", "png");

        return CreatePngTileResult(imageBytes, tileOptionsValue.CacheMaxAge);
    }

    private static async Task<IResult> ExecuteDatasetVectorTileAsync(
        HttpContext context,
        ITileProvider tileProvider,
        TileRequestLayer[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
        TemporalFilter? temporalFilter,
        TileOptions tileOptionsValue,
        TileLimits tileLimits,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (layers.Length > 1)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Dataset vector tiles currently support only a single collection selection. Request one collection or use PNG tiles.");
        }

        var mergedTile = await MergeVectorTileAsync(
            tileProvider,
            layers,
            tileCol,
            tileRow,
            zoomLevel,
            isGeographic,
            temporalFilter,
            tileOptionsValue,
            tileLimits,
            cancellationToken);

        if (mergedTile == null || mergedTile.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            // Cache empty tiles for the same window as populated ones so clients do
            // not revalidate on every pan/zoom over sparse regions.
            context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptionsValue.CacheMaxAge}";
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", mergedTile.Length);
        context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptionsValue.CacheMaxAge}";
        return Results.Bytes(mergedTile, MediaTypes.Mvt);
    }

    private static async Task<IResult> HandleDatasetRasterTileAsync(
        HttpContext context,
        TileRequestLayer[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
        IReadOnlyDictionary<int, TemporalFilter?> temporalFilters,
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
                SpatialReferenceSrid = layer.SpatialReferenceSrid,
                OutputSrid = filterSrid,
                Limit = remainingBudget,
                TemporalFilter = GetTemporalFilterForLayer(layer, temporalFilters)
            };

            var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            renderedLayers.Add(new TileRenderer.TileRenderLayer(queryResult.Items, ToTileGeometryKind(layer.GeometryType)));
            totalFeatureCount += queryResult.Items.Length;
            remainingBudget -= queryResult.Items.Length;
        }

        if (renderedLayers.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptionsValue.CacheMaxAge}";
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
        TileRequestLayer[] layers,
        int tileCol,
        int tileRow,
        int zoomLevel,
        bool isGeographic,
        TemporalFilter? temporalFilter,
        TileOptions tileOptionsValue,
        TileLimits tileLimits,
        CancellationToken cancellationToken)
    {
        List<byte[]>? tileParts = null;
        var totalLength = 0;

        foreach (var layer in layers)
        {
            var query = CreateVectorTileQuery(
                layer.SpatialReferenceSrid,
                isGeographic,
                temporalFilter: temporalFilter);
            var tileData = await tileProvider.GetMvtTileAsync(
                layer.StorageLayerId,
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

    private static FeatureQuery CreateVectorTileQuery(
        int spatialReferenceSrid,
        bool isGeographic,
        TemporalFilter? temporalFilter)
        => VectorTileExecution.CreateQuery(
            spatialReferenceSrid,
            temporalFilter: temporalFilter) with
        {
            OutputSrid = isGeographic ? 4326 : 3857
        };

    private static SpatialFilter CreateBboxSpatialFilter(TileBounds bounds, int srid)
    {
        if (SpatialReference.Create(srid).IsGeographic && bounds.XMin > bounds.XMax)
        {
            var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
            var westernHemisphere = geometryFactory.CreatePolygon(
            [
                new Coordinate(bounds.XMin, bounds.YMin),
                new Coordinate(180.0, bounds.YMin),
                new Coordinate(180.0, bounds.YMax),
                new Coordinate(bounds.XMin, bounds.YMax),
                new Coordinate(bounds.XMin, bounds.YMin)
            ]);
            var easternHemisphere = geometryFactory.CreatePolygon(
            [
                new Coordinate(-180.0, bounds.YMin),
                new Coordinate(bounds.XMax, bounds.YMin),
                new Coordinate(bounds.XMax, bounds.YMax),
                new Coordinate(-180.0, bounds.YMax),
                new Coordinate(-180.0, bounds.YMin)
            ]);

            var multiPolygon = geometryFactory.CreateMultiPolygon([westernHemisphere, easternHemisphere]);
            return SpatialFilter.Create(BboxWkbWriter.Write(multiPolygon), SpatialRelationship.Intersects, srid);
        }

        return SpatialFilterHelpers.CreateBboxSpatialFilter(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax, srid);
    }

    private static IEnumerable<TileSetItem> BuildDatasetTileSetItems(
        string titleBase,
        string baseUrl,
        TileLimits tileLimits,
        string querySuffix,
        bool advertiseVectorTiles)
    {
        foreach (var descriptor in OgcTileMatrixSetDescriptors.Supported)
        {
            yield return BuildTileSetItemCore(
                $"{titleBase} ({descriptor.Id})",
                $"{baseUrl}/ogc/tiles/tiles/{descriptor.Id}{querySuffix}",
                $"{baseUrl}/ogc/tiles/tiles/{descriptor.Id}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}{querySuffix}",
                $"{baseUrl}/ogc/tiles/tileMatrixSets/{descriptor.Id}",
                "Dataset tileset metadata",
                tileLimits,
                descriptor,
                advertiseVectorTiles);
        }
    }

    private static IEnumerable<TileSetItem> BuildTileSetItems(
        TileRequestLayer layer,
        string baseUrl,
        TileLimits tileLimits,
        string? encodedCollectionId = null)
    {
        var collectionId = encodedCollectionId ?? Uri.EscapeDataString(layer.CollectionId);

        foreach (var descriptor in OgcTileMatrixSetDescriptors.Supported)
        {
            var href = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{descriptor.Id}";
            yield return BuildTileSetItemCore(
                $"{layer.DisplayName} ({descriptor.Id})",
                href,
                $"{href}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}",
                $"{baseUrl}/ogc/tiles/tileMatrixSets/{descriptor.Id}",
                "Tileset metadata",
                tileLimits,
                descriptor);
        }
    }

    private static TileSetItem BuildTileSetItemCore(
        string title,
        string tilesetHref,
        string tileTemplate,
        string tileMatrixSetHref,
        string selfLinkTitle,
        TileLimits tileLimits,
        OgcTileMatrixSetDescriptor descriptor,
        bool advertiseVectorTiles = true)
    {
        var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(descriptor.Id);
        var matrixLimits = isGeographic
            ? OgcTilesUtilities.BuildWorldCrs84QuadLimits(tileLimits)
            : BuildTileMatrixSetLimits(tileLimits);
        var itemMediaType = advertiseVectorTiles ? MediaTypes.Mvt : MediaTypes.Png;
        var itemTitle = advertiseVectorTiles ? "Vector tiles" : "PNG tiles";
        var itemHref = advertiseVectorTiles
            ? tileTemplate
            : $"{tileTemplate}{(tileTemplate.Contains('?', StringComparison.Ordinal) ? "&" : "?")}f=png";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: selfLinkTitle),
            new Link
            {
                Href = itemHref,
                Rel = "item",
                Type = itemMediaType,
                Title = itemTitle,
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
            DataType = advertiseVectorTiles ? "vector" : "map",
            Crs = descriptor.Crs,
            TileMatrixSetId = descriptor.Id,
            TileMatrixSetUri = descriptor.Uri,
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
        string tileMatrixSetId,
        bool advertiseVectorTiles = true)
    {
        if (!OgcTileMatrixSetDescriptors.TryGet(tileMatrixSetId, out var descriptor))
        {
            throw new InvalidOperationException($"Unsupported tile matrix set '{tileMatrixSetId}'.");
        }

        var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId);
        var matrixLimits = isGeographic
            ? OgcTilesUtilities.BuildWorldCrs84QuadLimits(tileLimits)
            : BuildTileMatrixSetLimits(tileLimits);
        var itemMediaType = advertiseVectorTiles ? MediaTypes.Mvt : MediaTypes.Png;
        var itemTitle = advertiseVectorTiles ? "Vector tiles" : "PNG tiles";
        var itemHref = advertiseVectorTiles
            ? tileTemplate
            : $"{tileTemplate}{(tileTemplate.Contains('?', StringComparison.Ordinal) ? "&" : "?")}f=png";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: $"{titleBase} tileset"),
            new Link
            {
                Href = itemHref,
                Rel = "item",
                Type = itemMediaType,
                Title = itemTitle,
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
            DataType = advertiseVectorTiles ? "vector" : "map",
            Crs = descriptor.Crs,
            TileMatrixSetId = tileMatrixSetId,
            TileMatrixSetUri = descriptor.Uri,
            TileMatrixSetLimits = matrixLimits,
            Links = links,
            MediaTypes = advertiseVectorTiles
                ? ImmutableArray.Create(MediaTypes.Mvt, MediaTypes.Png)
                : ImmutableArray.Create(MediaTypes.Png)
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

    private static async Task<(TileRequestLayer[] Layers, IResult? Error)> ResolveDatasetLayersAsync(
        string? collections,
        IMetadataV2GraphProvider graphProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(collections))
        {
            var accessibleLayersByResource = new Dictionary<string, TileRequestLayer>(StringComparer.Ordinal);
            var requiresAuth = false;
            var hasDenied = false;

            foreach (var publication in snapshot.Graph.Publications)
            {
                if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                    !MetadataV2ServiceProtocols.IsProtocolEnabled(service, OgcApiTilesProtocol))
                {
                    continue;
                }

                var resource = snapshot.ResolveResource(publication);
                if (resource is null)
                {
                    continue;
                }

                var decision = AccessPolicyHelpers.EvaluateAccess(
                    context,
                    resource.AccessPolicy,
                    service.AccessPolicy);

                if (decision.IsAllowed)
                {
                    var layer = CreateTileRequestLayer(snapshot, publication, service, resource);
                    if (layer is not null &&
                        (!accessibleLayersByResource.TryGetValue(resource.Metadata.Id, out var existing) ||
                         (publication.IsPrimary && !existing.Publication.IsPrimary)))
                    {
                        accessibleLayersByResource[resource.Metadata.Id] = layer;
                    }
                    continue;
                }

                hasDenied = true;
                if (decision.RequiresAuthentication)
                {
                    requiresAuth = true;
                }
            }

            var accessibleLayersArray = accessibleLayersByResource.Values
                .OrderBy(layer => layer.PublicLayerId)
                .ThenBy(layer => layer.CollectionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (accessibleLayersArray.Length == 0)
            {
                if (hasDenied)
                {
                    return (Array.Empty<TileRequestLayer>(), requiresAuth
                        ? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage)
                        : StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage));
                }

                return (Array.Empty<TileRequestLayer>(), StandardErrorHelpers.CreateNotFound(context, "No collections are available."));
            }

            return (accessibleLayersArray, null);
        }

        if (!TryParseCollectionIds(collections, out var collectionIds, out var parseError))
        {
            return (Array.Empty<TileRequestLayer>(), StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid collections parameter."));
        }

        var selectedLayers = new List<TileRequestLayer>(collectionIds.Length);
        foreach (var collectionId in collectionIds)
        {
            var resolution = ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: true);
            if (resolution.Error is not null)
            {
                return (Array.Empty<TileRequestLayer>(), resolution.Error);
            }

            selectedLayers.Add(resolution.Layer!);
        }

        return (selectedLayers
            .OrderBy(layer => layer.PublicLayerId)
            .ThenBy(layer => layer.CollectionId, StringComparer.OrdinalIgnoreCase)
            .ToArray(), null);
    }

    private static bool TryParseCollectionIds(string collections, out string[] collectionIds, out string? error)
    {
        collectionIds = Array.Empty<string>();
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

        var parsedIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawValue in values)
        {
            var value = ExtractCollectionId(rawValue);
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "Invalid collections parameter.";
                return false;
            }

            parsedIds.Add(value);
        }

        if (parsedIds.Count == 0)
        {
            error = "Invalid collections parameter.";
            return false;
        }

        collectionIds = parsedIds.ToArray();

        return true;
    }

    private static TileRequestLayerResolution ResolveCollectionLayer(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        string collectionId,
        bool missingCollectionIsBadRequest)
    {
        var publication = FindCollectionPublication(snapshot, collectionId, requireProtocol: true, out var service);
        if (publication is null || service is null)
        {
            var existsWithoutProtocol = FindCollectionPublication(snapshot, collectionId, requireProtocol: false, out _) is not null;
            var error = existsWithoutProtocol || !missingCollectionIsBadRequest
                ? StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.")
                : StandardErrorHelpers.CreateBadRequest(context, $"Collection '{collectionId}' not found.");
            return new TileRequestLayerResolution(null, error);
        }

        var resource = snapshot.ResolveResource(publication);
        if (resource is null)
        {
            var error = missingCollectionIsBadRequest
                ? StandardErrorHelpers.CreateBadRequest(context, $"Collection '{collectionId}' not found.")
                : StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            return new TileRequestLayerResolution(null, error);
        }

        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
        if (accessError is not null)
        {
            return new TileRequestLayerResolution(null, accessError);
        }

        var layer = CreateTileRequestLayer(snapshot, publication, service, resource);
        if (layer is null)
        {
            return new TileRequestLayerResolution(
                null,
                StandardErrorHelpers.CreateInternalServerError(
                    context,
                    $"Collection '{collectionId}' is not bound to a storage layer."));
        }

        return new TileRequestLayerResolution(layer, null);
    }

    private static MetadataV2Publication? FindCollectionPublication(
        MetadataV2GraphSnapshot snapshot,
        string collectionId,
        bool requireProtocol,
        out MetadataV2Service? service)
    {
        service = null;
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!MatchesCollectionId(snapshot, publication, collectionId))
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var candidateService))
            {
                continue;
            }

            if (requireProtocol &&
                !MetadataV2ServiceProtocols.IsProtocolEnabled(candidateService, OgcApiTilesProtocol))
            {
                continue;
            }

            service = candidateService;
            return publication;
        }

        return null;
    }

    private static bool MatchesCollectionId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        string collectionId)
    {
        if (string.Equals(publication.ServiceLocalId, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Path, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Title, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var resource = snapshot.ResolveResource(publication);
        return resource is not null &&
            (string.Equals(resource.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resource.Metadata.Title, collectionId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resource.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase));
    }

    private static TileRequestLayer? CreateTileRequestLayer(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Service service,
        MetadataV2Resource resource)
    {
        var storageLayerId = snapshot.ResolveStorageLayerId(publication) ?? snapshot.ResolveStorageLayerId(resource);
        if (!storageLayerId.HasValue)
        {
            return null;
        }

        var publicLayerId = publication.LayerIndex ?? storageLayerId.Value;
        var collectionId = ResolveCollectionId(publication, resource);
        var displayName = publication.TitleOverride ?? resource.Metadata.Title ?? resource.Metadata.Name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = $"Layer {publicLayerId}";
        }

        return new TileRequestLayer(
            collectionId,
            publicLayerId,
            storageLayerId.Value,
            displayName,
            resource.Metadata.Description,
            resource,
            publication,
            service);
    }

    private static string ResolveCollectionId(MetadataV2Publication publication, MetadataV2Resource resource)
    {
        var collectionId = publication.ServiceLocalId ?? publication.Path ?? resource.Metadata.Name;
        return string.IsNullOrWhiteSpace(collectionId) ? resource.Metadata.Id : collectionId;
    }

    private static string BuildDatasetTitleBase(TileRequestLayer[] layers)
    {
        if (layers.Length == 1)
        {
            return layers[0].DisplayName;
        }

        return "Dataset";
    }

    private static string BuildCollectionsQuerySuffix(string? requestedCollections, TileRequestLayer[] layers)
    {
        if (string.IsNullOrWhiteSpace(requestedCollections))
        {
            return string.Empty;
        }

        var canonicalCollections = string.Join(
            ",",
            layers.Select(layer => Uri.EscapeDataString(layer.CollectionId)));
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
        TileRequestLayer[] layers,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        string tileMatrixSetId,
        out IReadOnlyDictionary<int, TemporalFilter?> temporalFilters)
    {
        var parsedTemporalFilters = new Dictionary<int, TemporalFilter?>(layers.Length);
        temporalFilters = parsedTemporalFilters;

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

        foreach (var layer in layers)
        {
            if (!OgcTemporalFilterParser.TryParse(datetime, layer.Resource, out var temporalFilter, out var errorMessage))
            {
                var detail = layers.Length > 1
                    ? $"Collection '{layer.CollectionId}' cannot satisfy the requested datetime filter. {errorMessage ?? "Invalid datetime parameter."}"
                    : errorMessage ?? "Invalid datetime parameter.";
                return StandardErrorHelpers.CreateBadRequest(context, detail);
            }

            parsedTemporalFilters[layer.StorageLayerId] = temporalFilter;
        }

        return null;
    }

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Error,
        Message = "Failed to generate OGC Tiles response for layers {LayerIds}, matrix set {TileMatrixSetId}, z={Zoom}, x={TileCol}, y={TileRow}.")]
    private static partial void LogTileResponseGenerationFailed(
        ILogger logger,
        string layerIds,
        string tileMatrixSetId,
        int zoom,
        int tileCol,
        int tileRow,
        Exception exception);

    private static TemporalFilter? GetTemporalFilterForLayer(
        TileRequestLayer layer,
        IReadOnlyDictionary<int, TemporalFilter?> temporalFilters)
        => temporalFilters.TryGetValue(layer.StorageLayerId, out var temporalFilter) ? temporalFilter : null;

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

    private static TileRenderer.GeometryKind ToTileGeometryKind(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => TileRenderer.GeometryKind.Point,
            MetadataV2GeometryType.MultiPoint => TileRenderer.GeometryKind.MultiPoint,
            MetadataV2GeometryType.LineString => TileRenderer.GeometryKind.LineString,
            MetadataV2GeometryType.MultiLineString => TileRenderer.GeometryKind.MultiLineString,
            MetadataV2GeometryType.Polygon => TileRenderer.GeometryKind.Polygon,
            MetadataV2GeometryType.MultiPolygon => TileRenderer.GeometryKind.MultiPolygon,
            _ => TileRenderer.GeometryKind.Unknown
        };

    private sealed record TileRequestLayer(
        string CollectionId,
        int PublicLayerId,
        int StorageLayerId,
        string DisplayName,
        string? Description,
        MetadataV2Resource Resource,
        MetadataV2Publication Publication,
        MetadataV2Service Service)
    {
        public int SpatialReferenceSrid => Resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;

        public MetadataV2GeometryType GeometryType => Resource.ReadGeometryType();
    }

    private readonly record struct TileRequestLayerResolution(TileRequestLayer? Layer, IResult? Error);

}
