// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcTiles.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OgcTiles;

internal static class TilesEndpoints
{
    public static IEndpointRouteBuilder MapTilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/tiles/tiles", HandleGetDatasetTilesets)
            .WithDisplayName("OGC API Tiles Tilesets List")
            .WithName("OgcTilesTilesets")
            .WithSummary("Get available tilesets for the dataset")
            .WithDescription("Lists vector tilesets for the dataset")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTilesets")
            .Produces<TileSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/tiles/{tileMatrixSetId}", HandleGetDatasetTileset)
            .WithDisplayName("OGC API Tiles Dataset Tileset")
            .WithName("OgcTilesDatasetTileset")
            .WithSummary("Get tileset metadata for the dataset")
            .WithDescription("Returns tileset metadata for the dataset and tile matrix set")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTileset")
            .Produces<TileSet>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetDatasetTile)
            .WithDisplayName("OGC API Tiles Dataset Tile")
            .WithName("OgcTilesDatasetTile")
            .WithSummary("Get a vector tile for the dataset")
            .WithDescription("Returns a Mapbox Vector Tile for the dataset and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTile");

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles", HandleGetCollectionTilesets)
            .WithDisplayName("OGC API Tiles Collection Tilesets List")
            .WithName("OgcTilesCollectionTilesets")
            .WithSummary("Get available tilesets for a collection")
            .WithDescription("Lists vector tilesets for the specified collection")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollectionTilesets")
            .Produces<TileSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}", HandleGetCollectionTileset)
            .WithDisplayName("OGC API Tiles Collection Tileset")
            .WithName("OgcTilesCollectionTileset")
            .WithSummary("Get tileset metadata for a collection")
            .WithDescription("Returns tileset metadata for the specified collection and tile matrix set")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollectionTileset")
            .Produces<TileSet>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetTile)
            .WithDisplayName("OGC API Tiles Collection Tile")
            .WithName("OgcTilesCollectionTile")
            .WithSummary("Get a vector tile for a collection")
            .WithDescription("Returns a Mapbox Vector Tile for the specified collection and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTile");

        return endpoints;
    }

    private static IResult HandleGetDatasetTilesets(
        HttpContext context,
        string? f)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        var tilesets = ImmutableArray.Create(BuildDatasetTileSetItem(baseUrl));

        var links = OgcFeaturesUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/tiles",
                outputFormat,
                OgcFeaturesUtilities.MetadataFormats,
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

        return OgcFeaturesUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileSetsList, outputFormat, "Tilesets");
    }

    private static async Task<IResult> HandleGetDatasetTileset(
        string tileMatrixSetId,
        HttpContext context,
        string? f,
        string? collections,
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.DatasetTilesetMetadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layer, layerError) = await ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileTemplate = $"{tilesetHref}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";
        var geodataHref = string.IsNullOrWhiteSpace(collections)
            ? $"{baseUrl}/ogc/features/collections"
            : $"{baseUrl}/ogc/features/collections/{layer!.Id}";
        var titleBase = string.IsNullOrWhiteSpace(collections) ? "Dataset" : layer!.Name;

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
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt)
        };

        return OgcFeaturesUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetDatasetTile(
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        string? f,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        string? collections,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IOptions<TileOptions> tileOptions)
    {
        _ = datetime;
        _ = subset;
        _ = crs;
        _ = subsetCrs;

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.DatasetTiles);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "mvt", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not supported.");
        }

        if (!OgcTilesUtilities.AcceptsVectorTiles(context.Request))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not acceptable.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var options = tileOptions.Value;
        if (zoomLevel < options.MinZoom || zoomLevel > options.MaxZoom)
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
        }

        if (!TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel))
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var (layer, layerError) = await ResolveDatasetLayerAsync(collections, layerCatalog, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var tileData = await featureStore.GetMvtTileAsync(layer!.Id, tileCol, tileRow, zoomLevel, null, options, cancellationToken);
        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        context.Response.Headers["Cache-Control"] = $"public, max-age={options.CacheMaxAge}";
        return Results.Bytes(tileData, MediaTypes.Mvt);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        if (!int.TryParse(collectionId, out var layerId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var tilesets = ImmutableArray.Create(BuildTileSetItem(layerId, layer.Name, baseUrl));
        var links = OgcFeaturesUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles",
                outputFormat,
                OgcFeaturesUtilities.MetadataFormats,
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

        return OgcFeaturesUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileSetsList, outputFormat, "Tilesets");
    }

    private static async Task<IResult> HandleGetCollectionTileset(
        string collectionId,
        string tileMatrixSetId,
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        if (!int.TryParse(collectionId, out var layerId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

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
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            Links = links,
            MediaTypes = ImmutableArray.Create(MediaTypes.Mvt)
        };

        return OgcFeaturesUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    private static async Task<IResult> HandleGetTile(
        string collectionId,
        string tileMatrixSetId,
        string tileMatrix,
        int tileRow,
        int tileCol,
        string? f,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IOptions<TileOptions> tileOptions)
    {
        _ = datetime;
        _ = subset;
        _ = crs;
        _ = subsetCrs;

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Tiles);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "mvt", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not supported.");
        }

        if (!OgcTilesUtilities.AcceptsVectorTiles(context.Request))
        {
            return ProtocolErrorWriter.CreateErrorResult(context, StatusCodes.Status406NotAcceptable, "Not Acceptable", "Requested tile format is not acceptable.");
        }

        if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var options = tileOptions.Value;
        if (zoomLevel < options.MinZoom || zoomLevel > options.MaxZoom)
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
        }

        if (!TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel))
        {
            return OgcErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        var tileData = await featureStore.GetMvtTileAsync(layerId, tileCol, tileRow, zoomLevel, null, options, cancellationToken);
        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        context.Response.Headers["Cache-Control"] = $"public, max-age={options.CacheMaxAge}";
        return Results.Bytes(tileData, MediaTypes.Mvt);
    }

    private static TileSetItem BuildDatasetTileSetItem(string baseUrl)
    {
        var tilesetHref = $"{baseUrl}/ogc/tiles/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Dataset tileset metadata"),
            Link.Create(
                href: tileMatrixSetHref,
                rel: RelationTypes.TilingScheme,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"));

        return new TileSetItem
        {
            Title = $"Dataset ({OgcTilesUtilities.WebMercatorQuadId})",
            DataType = "vector",
            Crs = OgcTilesUtilities.WebMercatorCrs,
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            Links = links
        };
    }

    private static TileSetItem BuildTileSetItem(int layerId, string? layerName, string baseUrl)
    {
        var collectionId = layerId.ToString(CultureInfo.InvariantCulture);
        var tilesetHref = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{OgcTilesUtilities.WebMercatorQuadId}";
        var tileMatrixSetHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{OgcTilesUtilities.WebMercatorQuadId}";

        var links = ImmutableArray.Create(
            Link.Create(
                href: tilesetHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Tileset metadata"),
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
            TileMatrixSetUri = OgcTilesUtilities.WebMercatorQuadUri,
            Links = links
        };
    }

    private static async Task<(LayerDefinition? Layer, IResult? Error)> ResolveDatasetLayerAsync(
        string? collections,
        ILayerCatalog layerCatalog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collections))
        {
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var layer = layers.OrderBy(l => l.Id).FirstOrDefault();
            if (layer == null)
            {
                return (null, OgcErrorHelpers.CreateNotFound(context, "No collections are available."));
            }

            return (layer, null);
        }

        if (!TryParseCollectionId(collections, out var collectionId, out var parseError))
        {
            return (null, OgcErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid collections parameter."));
        }

        var selectedLayer = await layerCatalog.GetLayerAsync(collectionId, cancellationToken);
        if (selectedLayer == null)
        {
            return (null, OgcErrorHelpers.CreateBadRequest(context, $"Collection '{collectionId}' not found."));
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

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return OgcErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return OgcErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }
}
