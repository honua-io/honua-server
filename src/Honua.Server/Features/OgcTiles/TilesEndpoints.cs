// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
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
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcTiles.Models;
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
            .WithDescription("Lists vector tilesets for the dataset")
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
            .WithSummary("Get a vector tile for the dataset")
            .WithDescription("Returns a Mapbox Vector Tile for the dataset and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTile");

        var collectionTilesets = endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles", HandleGetCollectionTilesets)
            .WithDisplayName("OGC API Tiles Collection Tilesets List")
            .WithName("OgcTilesCollectionTilesets")
            .WithSummary("Get available tilesets for a collection")
            .WithDescription("Lists vector tilesets for the specified collection")
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
            .WithSummary("Get a vector tile for a collection")
            .WithDescription("Returns a Mapbox Vector Tile for the specified collection and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTile");

        return endpoints;
    }

    private static async Task<IResult> HandleGetDatasetTilesets(
        HttpContext context,
        ILayerCatalog layerCatalog,
        IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var datetime = OgcCommonUtilities.GetQueryValue(request, "datetime");
        var subset = OgcCommonUtilities.GetQueryValue(request, "subset");
        var crs = OgcCommonUtilities.GetQueryValue(request, "crs");
        var subsetCrs = OgcCommonUtilities.GetQueryValue(request, "subset-crs");
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
        var layers = await layerCatalog.ListLayersAsync(cancellationToken);
        if (layers.Length == 0)
        {
            return StandardErrorHelpers.CreateNotFound(context, "No collections are available.");
        }

        var accessibleLayers = layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer))
            .OrderBy(layer => layer.Id)
            .ToArray();

        if (accessibleLayers.Length == 0)
        {
            return AccessPolicyHelpers.RequireAnyLayerAccess(context, layers)!;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var tilesets = accessibleLayers
            .Select(layer => BuildDatasetTileSetItem(layer, baseUrl, tileLimits))
            .ToImmutableArray();
        return BuildTilesetsListResponse(
            request,
            $"{baseUrl}/ogc/tiles/tiles",
            $"{baseUrl}/ogc/tiles",
            "Landing page",
            tilesets,
            outputFormat);
    }

    private static async Task<IResult> HandleGetDatasetTileset(
        string tileMatrixSetId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IOptions<LimitsOptions> limitsOptions)
    {
        var request = context.Request;
        var f = OgcCommonUtilities.GetQueryValue(request, "f");
        var collections = OgcCommonUtilities.GetQueryValue(request, "collections");
        var datetime = OgcCommonUtilities.GetQueryValue(request, "datetime");
        var subset = OgcCommonUtilities.GetQueryValue(request, "subset");
        var crs = OgcCommonUtilities.GetQueryValue(request, "crs");
        var subsetCrs = OgcCommonUtilities.GetQueryValue(request, "subset-crs");
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
        var (layer, layerError) = await ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var collectionParam = $"collections={Uri.EscapeDataString(layer!.Id.ToString(CultureInfo.InvariantCulture))}";
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}?{collectionParam}";
        var tileTemplate = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?{collectionParam}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var geodataHref = $"{baseUrl}/ogc/features/collections/{layer!.Id}";
        var titleBase = string.IsNullOrWhiteSpace(layer!.Name) ? $"Layer {layer.Id}" : layer.Name;

        var tileset = BuildTileset(
            titleBase,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            geodataHref,
            "Geospatial data",
            layer?.Description,
            tileLimits);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetDatasetTileItem(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        ILayerCatalog layerCatalog,
        ITileProvider tileProvider,
        IOptions<TileOptions> tileOptions,
        IOptions<LimitsOptions> limitsOptions)
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
            cancellationToken => ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken),
            layer => layer.SpatialReference.Wkid,
            tileProvider,
            tileOptions,
            limitsOptions);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IOptions<LimitsOptions> limitsOptions)
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

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
        if (accessError != null)
        {
            return accessError;
        }

        var tileLimits = limitsOptions.Value.Tiles;
        var tilesets = ImmutableArray.Create(BuildTileSetItem(layerId, layer.Name, baseUrl, tileLimits));
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
        ILayerCatalog layerCatalog,
        IOptions<LimitsOptions> limitsOptions)
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
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var titleBase = string.IsNullOrWhiteSpace(layer.Name) ? $"Layer {layerId}" : layer.Name;

        var tileset = BuildTileset(
            titleBase,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            $"{baseUrl}/ogc/features/collections/{collectionId}",
            "Collection metadata",
            layer.Description,
            tileLimits);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetCollectionTile(
        string collectionId,
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context,
        ILayerCatalog layerCatalog,
        ITileProvider tileProvider,
        IOptions<TileOptions> tileOptions,
        IOptions<LimitsOptions> limitsOptions)
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
                    return (null, StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
                }

                var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
                if (layer == null)
                {
                    return (null, StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found."));
                }

                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
                if (accessError != null)
                {
                    return (null, accessError);
                }

                return (layer, null);
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
        Func<CancellationToken, Task<(LayerDefinition? Layer, IResult? Error)>> resolveLayerAsync,
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

        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "mvt", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotAcceptable(context, "Requested tile format is not supported.");
        }

        if (!OgcTilesUtilities.AcceptsVectorTiles(request))
        {
            return StandardErrorHelpers.CreateNotAcceptable(context, "Requested tile format is not acceptable.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

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

        if (!TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layer, layerError) = await resolveLayerAsync(cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        if (layer is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Collection not found.");
        }

        var validationResult = ValidateTileQueryParameters(context, layer, datetime, subset, crs, subsetCrs, out var temporalFilter);
        if (validationResult is not null)
        {
            return validationResult;
        }

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = getSpatialReferenceSrid(layer),
            TemporalFilter = temporalFilter
        };

        var tileData = await tileProvider.GetMvtTileAsync(layer.Id, tileCol, tileRow, zoomLevel, query, tileOptionsValue, tileLimits, cancellationToken);
        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        return CreateTileResult(tileData, tileOptionsValue.CacheMaxAge);
    }

    private static TileSetItem BuildDatasetTileSetItem(LayerDefinition layer, string baseUrl, TileLimits tileLimits)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var collectionParam = $"collections={Uri.EscapeDataString(collectionId)}";
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}?{collectionParam}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?{collectionParam}";
        var title = string.IsNullOrWhiteSpace(layer.Name)
            ? $"Layer {collectionId} ({OgcTilesUtilities.WebMercatorQuadId})"
            : $"{layer.Name} ({OgcTilesUtilities.WebMercatorQuadId})";
        return BuildTileSetItemCore(
            title,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            "Dataset tileset metadata",
            tileLimits);
    }

    private static TileSetItem BuildTileSetItem(int layerId, string? layerName, string baseUrl, TileLimits tileLimits)
    {
        var collectionId = layerId.ToString(CultureInfo.InvariantCulture);
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var title = string.IsNullOrWhiteSpace(layerName)
            ? $"Layer {layerId}"
            : $"{layerName} ({OgcTilesUtilities.WebMercatorQuadId})";
        return BuildTileSetItemCore(
            title,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            "Tileset metadata",
            tileLimits);
    }

    private static TileSetItem BuildTileSetItemCore(
        string title,
        string tilesetHref,
        string tileTemplate,
        string tileMatrixSetHref,
        string selfLinkTitle,
        TileLimits tileLimits)
    {
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
            Link.Create(
                href: tileMatrixSetHref,
                rel: RelationTypes.TilingScheme,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"));

        return new TileSetItem
        {
            Title = title,
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileLimits),
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
        TileLimits tileLimits)
    {
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
            Title = $"{titleBase} ({OgcTilesUtilities.WebMercatorQuadId})",
            Description = description,
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileLimits),
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt)
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
    {
        var minZoom = Math.Max(0, limits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, limits.MaxTileZoom);
        var matrixLimits = new List<TileMatrixSetLimit>();

        for (var zoom = minZoom; zoom <= maxZoom; zoom++)
        {
            var matrixSize = 1 << zoom;
            var maxIndex = matrixSize - 1;

            matrixLimits.Add(new TileMatrixSetLimit
            {
                TileMatrix = zoom.ToString(CultureInfo.InvariantCulture),
                MinTileRow = 0,
                MaxTileRow = maxIndex,
                MinTileCol = 0,
                MaxTileCol = maxIndex
            });
        }

        return matrixLimits.ToImmutableArray();
    }

    private static async Task<(LayerDefinition? Layer, IResult? Error)> ResolveDatasetLayerAsync(
        string? collections,
        ILayerCatalog layerCatalog,
        HttpContext context,
        CancellationToken cancellationToken,
        bool requireCollection = false)
    {
        if (string.IsNullOrWhiteSpace(collections))
        {
            if (requireCollection)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    "The collections parameter is required for dataset tiles."));
            }

            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            if (layers.Length == 0)
            {
                return (null, StandardErrorHelpers.CreateNotFound(context, "No collections are available."));
            }

            var accessibleLayers = layers
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer))
                .OrderBy(layer => layer.Id)
                .ToArray();

            if (accessibleLayers.Length == 0)
            {
                return (null, AccessPolicyHelpers.RequireAnyLayerAccess(context, layers));
            }

            return (accessibleLayers[0], null);
        }

        if (!TryParseCollectionId(collections, out var collectionId, out var parseError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid collections parameter."));
        }

        var selectedLayer = await layerCatalog.GetLayerAsync(collectionId, cancellationToken);
        if (selectedLayer == null)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, $"Collection '{collectionId}' not found."));
        }

        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, selectedLayer);
        if (accessError != null)
        {
            return (null, accessError);
        }

        return (selectedLayer, null);
    }

    private static bool TryParseCollectionId(string collections, out int collectionId, out string? error)
    {
        collectionId = default;
        error = null;

        var values = collections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            error = "Invalid collections parameter.";
            return false;
        }

        if (values.Length > 1)
        {
            error = "Multiple collections are not supported for dataset tiles.";
            return false;
        }

        var value = ExtractCollectionId(values[0]);
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Invalid collections parameter.";
            return false;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out collectionId))
        {
            error = $"Invalid collections parameter '{collections}'.";
            return false;
        }

        return true;
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

    private static IResult? ValidateTileQueryParameters(
        HttpContext context,
        LayerDefinition layer,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        out TemporalFilter? temporalFilter)
    {
        temporalFilter = null;

        if (!string.IsNullOrWhiteSpace(subset))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "The subset parameter is not supported.");
        }

        if (!IsWebMercatorCrs(crs))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported crs '{crs}'. Only EPSG:3857 is supported for tiles.");
        }

        if (!IsWebMercatorCrs(subsetCrs))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported subset-crs '{subsetCrs}'. Only EPSG:3857 is supported for tiles.");
        }

        if (!OgcTemporalFilterParser.TryParse(datetime, layer, out temporalFilter, out var errorMessage))
        {
            return StandardErrorHelpers.CreateBadRequest(context, errorMessage ?? "Invalid datetime parameter.");
        }

        return null;
    }

    private static bool IsWebMercatorCrs(string? crs)
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

}
