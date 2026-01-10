// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
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
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");
        var datetime = GetQueryValue(request, "datetime");
        var subset = GetQueryValue(request, "subset");
        var crs = GetQueryValue(request, "crs");
        var subsetCrs = GetQueryValue(request, "subset-crs");
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
            return AccessPolicyHelpers.RequireAnyLayerAccess(context, layers);
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tilesets = accessibleLayers
            .Select(layer => BuildDatasetTileSetItem(layer, baseUrl, tileOptions))
            .ToImmutableArray();

        var links = OgcCommonUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/tiles",
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Tilesets")
            .ToBuilder();

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Landing page"));

        var response = new TileSetsList
        {
            Tilesets = tilesets,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileSetsList, outputFormat, "Tilesets");
    }

    private static async Task<IResult> HandleGetDatasetTileset(
        string tileMatrixSetId,
        HttpContext context)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");
        var collections = GetQueryValue(request, "collections");
        var datetime = GetQueryValue(request, "datetime");
        var subset = GetQueryValue(request, "subset");
        var crs = GetQueryValue(request, "crs");
        var subsetCrs = GetQueryValue(request, "subset-crs");
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

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layer, layerError) = await ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken, requireCollection: true);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var collectionParam = $"collections={Uri.EscapeDataString(layer!.Id.ToString(CultureInfo.InvariantCulture))}";
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}?{collectionParam}";
        var tileTemplate = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?{collectionParam}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var geodataHref = $"{baseUrl}/ogc/features/collections/{layer!.Id}";
        var titleBase = string.IsNullOrWhiteSpace(layer!.Name) ? $"Layer {layer.Id}" : layer.Name;

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
                title: "Geospatial data")
        );

        var tileset = new TileSet
        {
            Title = $"{titleBase} ({OgcTilesUtilities.WebMercatorQuadId})",
            Description = layer?.Description,
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileOptions),
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt)
        };

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetDatasetTileItem(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");
        var collections = GetQueryValue(request, "collections");

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.DatasetTiles);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "mvt", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not supported.");
        }

        if (!OgcTilesUtilities.AcceptsVectorTiles(request))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not acceptable.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        var options = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        if (zoomLevel < options.MinZoom || zoomLevel > options.MaxZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
        }

        if (!TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layer, layerError) = await ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken, requireCollection: true);
        if (layerError is not null)
        {
            return layerError;
        }

        var validationResult = ValidateTileQueryParameters(context, layer!, datetime, subset, crs, subsetCrs, out var temporalFilter);
        if (validationResult is not null)
        {
            return validationResult;
        }

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = layer!.SpatialReference.Wkid,
            TemporalFilter = temporalFilter
        };
        var tileData = await tileProvider.GetMvtTileAsync(layer.Id, tileCol, tileRow, zoomLevel, query, options, cancellationToken);
        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        return CreateTileResult(tileData, options.CacheMaxAge);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");
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

        if (!int.TryParse(collectionId, out var layerId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
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

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tilesets = ImmutableArray.Create(BuildTileSetItem(layerId, layer.Name, baseUrl, tileOptions));
        var links = OgcCommonUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles",
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Tilesets")
            .ToBuilder();

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/collections/{collectionId}",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Collection"));

        var response = new TileSetsList
        {
            Tilesets = tilesets,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileSetsList, outputFormat, "Tilesets");
    }

    private static async Task<IResult> HandleGetCollectionTileset(
        string collectionId,
        string tileMatrixSetId,
        HttpContext context)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");
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

        if (!int.TryParse(collectionId, out var layerId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
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

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: $"{layer.Name} tileset"),
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
                href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                rel: RelationTypes.Geodata,
                type: MediaTypes.Json,
                title: "Collection metadata")
        );

        var tileset = new TileSet
        {
            Title = $"{layer.Name} ({OgcTilesUtilities.WebMercatorQuadId})",
            Description = layer.Description,
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileOptions),
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt)
        };

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetCollectionTile(
        string collectionId,
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        HttpContext context)
    {
        var request = context.Request;
        var f = GetQueryValue(request, "f");

        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Tiles);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "mvt", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not supported.");
        }

        if (!OgcTilesUtilities.AcceptsVectorTiles(request))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not acceptable.");
        }

        if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var layerCatalog = context.RequestServices.GetRequiredService<ILayerCatalog>();
        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        var options = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        if (zoomLevel < options.MinZoom || zoomLevel > options.MaxZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
        }

        if (!TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
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

        var validationResult = ValidateTileQueryParameters(context, layer, datetime, subset, crs, subsetCrs, out var temporalFilter);
        if (validationResult is not null)
        {
            return validationResult;
        }

        var query = new FeatureQuery
        {
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            TemporalFilter = temporalFilter
        };
        var tileData = await tileProvider.GetMvtTileAsync(layerId, tileCol, tileRow, zoomLevel, query, options, cancellationToken);
        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        return CreateTileResult(tileData, options.CacheMaxAge);
    }

    private static TileSetItem BuildDatasetTileSetItem(LayerDefinition layer, string baseUrl, TileOptions tileOptions)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var collectionParam = $"collections={Uri.EscapeDataString(collectionId)}";
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}?{collectionParam}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}?{collectionParam}";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Dataset tileset metadata"),
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
            Title = string.IsNullOrWhiteSpace(layer.Name)
                ? $"Layer {collectionId} ({OgcTilesUtilities.WebMercatorQuadId})"
                : $"{layer.Name} ({OgcTilesUtilities.WebMercatorQuadId})",
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileOptions),
            Links = links
        };
    }

    private static TileSetItem BuildTileSetItem(int layerId, string? layerName, string baseUrl, TileOptions tileOptions)
    {
        var collectionId = layerId.ToString(CultureInfo.InvariantCulture);
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Tileset metadata"),
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
            Title = string.IsNullOrWhiteSpace(layerName) ? $"Layer {layerId}" : $"{layerName} ({OgcTilesUtilities.WebMercatorQuadId})",
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetId = OgcTilesUtilities.WebMercatorQuadId,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            TileMatrixSetLimits = BuildTileMatrixSetLimits(tileOptions),
            Links = links
        };
    }

    private static ImmutableArray<TileMatrixSetLimit> BuildTileMatrixSetLimits(TileOptions options)
    {
        var minZoom = Math.Max(0, options.MinZoom);
        var maxZoom = Math.Max(minZoom, options.MaxZoom);
        var limits = new List<TileMatrixSetLimit>();

        for (var zoom = minZoom; zoom <= maxZoom; zoom++)
        {
            var matrixSize = 1 << zoom;
            var maxIndex = matrixSize - 1;

            limits.Add(new TileMatrixSetLimit
            {
                TileMatrix = zoom.ToString(CultureInfo.InvariantCulture),
                MinTileRow = 0,
                MaxTileRow = maxIndex,
                MinTileCol = 0,
                MaxTileCol = maxIndex
            });
        }

        return limits.ToImmutableArray();
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
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                StatusCodes.Status406NotAcceptable,
                "Not Acceptable",
                "Requested format is not acceptable.");
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

    private static string? GetQueryValue(HttpRequest request, string key)
    {
        if (!request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
