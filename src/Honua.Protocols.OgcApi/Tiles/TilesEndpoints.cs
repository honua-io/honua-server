// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Api.Tiles.Models;
using Honua.Protocols.Ogc.Api.Tiles.Services;
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

    public static IEndpointRouteBuilder MapTilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/tiles/tiles", HandleGetDatasetTilesets)
            .WithDisplayName("OGC API Tiles Tilesets List")
            .WithName("OgcTilesTilesets")
            .WithSummary("Get available tilesets for the dataset")
            .WithDescription("Lists available tilesets for the dataset")
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

        endpoints.MapGet("/ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetDatasetTileItem)
            .WithDisplayName("OGC API Tiles Dataset Tile")
            .WithName("OgcTilesDatasetTile")
            .WithSummary("Get a tile for the dataset")
            .WithDescription("Returns a Mapbox Vector Tile or PNG tile for the dataset and tile coordinates")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesDatasetTile");

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles", HandleGetCollectionTilesets)
            .WithDisplayName("OGC API Tiles Collection Tilesets List")
            .WithName("OgcTilesCollectionTilesets")
            .WithSummary("Get available tilesets for a collection")
            .WithDescription("Lists available tilesets for the specified collection")
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

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}", HandleGetCollectionTile)
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
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
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
        var tilesets = BuildDatasetTileSetItems(titleBase, baseUrl, tileLimits, querySuffix, advertiseVectorTiles, tileMatrixSetRegistry).ToImmutableArray();
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
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
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

        // The per-dataset tileset-metadata document is built for any registered gridset: the two
        // built-in pyramids keep the byte-identical static-descriptor path, while a custom
        // (operator-defined) gridset is advertised from its registered entry + geometry (#1916).
        if (!tileMatrixSetRegistry.TryGet(tileMatrixSetId, out var tileMatrixSetEntry))
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

        var (customEntry, customGeometry) = ResolveCustomTilesetGrid(tileMatrixSetEntry, tileMatrixSetRegistry, limitsOptions);

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
            advertiseVectorTiles: layers.Length == 1,
            customEntry,
            customGeometry);

        return OgcCommonUtilities.FormatMetadataResponse(tileset, OgcTilesJsonContext.Default.TileSet, outputFormat, "Tileset");
    }

    // Resolves the custom-gridset entry + geometry to advertise in a per-dataset/per-collection
    // tileset document. Returns (null, null) for the two built-in gridsets so BuildTileset keeps
    // the byte-identical static-descriptor path that the CITE snapshots depend on (#1916).
    private static (TileMatrixSetEntry? Entry, GridGeometry? Geometry) ResolveCustomTilesetGrid(
        TileMatrixSetEntry entry,
        ITileMatrixSetRegistry registry,
        IOptions<LimitsOptions> limitsOptions)
    {
        if (entry.IsBuiltIn)
        {
            return (null, null);
        }

        // Custom gridsets carry their own explicit levels (maxLevel is ignored for them).
        var maxLevel = Math.Max(0, limitsOptions.Value.Tiles.MaxTileZoom);
        return registry.TryGetGeometry(entry.Id, maxLevel, out var geometry)
            ? (entry, geometry)
            : (entry, null);
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
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
    {
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

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
            limitsOptions,
            tileMatrixSetRegistry);
    }

    private static async Task<IResult> HandleGetCollectionTilesets(
        string collectionId,
        HttpContext context,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
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
        var resolution = await ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var layer = resolution.Layer!;
        var tileLimits = limitsOptions.Value.Tiles;
        var encodedCollectionId = Uri.EscapeDataString(collectionId);
        var tilesets = BuildTileSetItems(layer, baseUrl, tileLimits, encodedCollectionId, tileMatrixSetRegistry).ToImmutableArray();
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
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
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

        // The per-collection tileset-metadata document is built for any registered gridset: the two
        // built-in pyramids keep the byte-identical static-descriptor path, while a custom
        // (operator-defined) gridset is advertised from its registered entry + geometry (#1916).
        if (!tileMatrixSetRegistry.TryGet(tileMatrixSetId, out var tileMatrixSetEntry))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolution = await ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false, cancellationToken).ConfigureAwait(false);
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

        var (customEntry, customGeometry) = ResolveCustomTilesetGrid(tileMatrixSetEntry, tileMatrixSetRegistry, limitsOptions);

        var tileset = BuildTileset(
            layer.DisplayName,
            tilesetHref,
            tileTemplate,
            tileMatrixSetHref,
            $"{baseUrl}/ogc/features/collections/{encodedCollectionId}",
            "Collection metadata",
            layer.Description,
            tileLimits,
            tileMatrixSetId,
            advertiseVectorTiles: true,
            customEntry,
            customGeometry);

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
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        [FromServices] ITileMatrixSetRegistry tileMatrixSetRegistry)
    {
        if (!TryRequireTenantContext(context, out var tenantError))
        {
            return tenantError!;
        }

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
                var resolution = await ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: false, cancellationToken).ConfigureAwait(false);
                if (resolution.Error is not null)
                {
                    return (Array.Empty<TileRequestLayer>(), resolution.Error);
                }

                return (new[] { resolution.Layer! }, null);
            },
            tileProvider,
            tileOptions,
            limitsOptions,
            tileMatrixSetRegistry);
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
        IOptions<LimitsOptions> limitsOptions,
        ITileMatrixSetRegistry tileMatrixSetRegistry)
    {
        // Resolved from the request container rather than taken as a formal parameter
        // (honua-server#2962): this method and its callers are already at the
        // architecture-enforced endpoint injected-parameter ceiling (DependencyInjectionLimitsTests),
        // and FeatureProviderQueryRouter is optional (null in hosts/tests that never register a
        // secondary/additional provider), matching the DI-optional pattern already used for
        // OData/OGC API Features provider routing.
        var providerQueryRouter = context.RequestServices.GetService<FeatureProviderQueryRouter>();
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

        if (!tileMatrixSetRegistry.TryGet(tileMatrixSetId, out var tileMatrixSetEntry))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var isGeographic = tileMatrixSetEntry.IsGeographic;

        if (!int.TryParse(tileMatrix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoomLevel))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile matrix '{tileMatrix}'.");
        }

        var tileOptionsValue = tileOptions.Value;
        var tileLimits = limitsOptions.Value.Tiles;

        // Resolve the grid geometry. Built-in gridsets generate levels for the configured zoom
        // range and are still subject to the server-wide tile-zoom limits; custom gridsets are
        // validated against their own configured level set.
        if (!tileMatrixSetRegistry.TryGetGeometry(tileMatrixSetEntry.Id, Math.Max(0, tileLimits.MaxTileZoom), out var gridGeometry))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        bool validCoords;
        if (tileMatrixSetEntry.IsBuiltIn)
        {
            if (zoomLevel < tileLimits.MinTileZoom || zoomLevel > tileLimits.MaxTileZoom)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
            }

            validCoords = isGeographic
                ? TileMath.ValidateTileCoordinatesGeographic(tileCol, tileRow, zoomLevel)
                : TileMath.ValidateTileCoordinates(tileCol, tileRow, zoomLevel);
        }
        else
        {
            if (gridGeometry.FindLevel(zoomLevel) is not { } customLevel)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Tile matrix '{tileMatrix}' is outside supported range.");
            }

            validCoords = tileCol >= 0 && tileRow >= 0 &&
                tileCol < customLevel.MatrixWidth && tileRow < customLevel.MatrixHeight;
        }

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
                tileMatrixSetEntry,
                out var temporalFilters,
                out var verticalSelections);
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

            // DOCUMENTED DIVERGENCE (#1792 Shape A): the vertical (elevation) selection
            // parsed from `subset=Z(...)` is RECORDED here (threaded into the raster/vector
            // handlers, telemetry-tagged) but does NOT change the rendered/queried output —
            // there is no Z-aware vector or raster source yet. This is an intentional, honest
            // divergence per AGENTS.md ("intentionally diverges" rule): the previous behavior
            // blanket-rejected every `subset`; we now accept + record the vertical axis while
            // still rejecting unknown axes (CITE guard). Applying the Z-slice to a Zarr
            // datacube render is the deferred Shape B follow-up (after #1790). Direct endpoint
            // tests cover this record-but-don't-render behavior.

            // Tile envelope in the gridset CRS. For the two built-ins this is byte-identical to
            // the legacy TileMath.GetTileBounds / GetTileBoundsGeographic output (the registry
            // seeds those geometries from the same formulas); for custom gridsets the envelope is
            // derived from the configured origin / cell size.
            var tileBounds = gridGeometry.GetTileBounds(tileCol, tileRow, zoomLevel);
            if (tileBounds is null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Invalid tile coordinates: row={tileRow}, col={tileCol}, matrix={tileMatrix}.");
            }

            var filterSrid = gridGeometry.Srid;

            // Raster (PNG) tile path
            if (isRaster)
            {
                var renderContext = new RasterTileRenderContext(
                    tileBounds,
                    filterSrid,
                    tileLimits,
                    tileOptionsValue,
                    activity);

                return layers.Length == 1
                    ? await HandleRasterTileAsync(
                        context,
                        layer,
                        renderContext,
                        GetTemporalFilterForLayer(layer, temporalFilters),
                        GetVerticalSelectionForLayer(layer, verticalSelections),
                        providerQueryRouter,
                        cancellationToken)
                    : await HandleDatasetRasterTileAsync(
                        context,
                        layers,
                        renderContext,
                        temporalFilters,
                        providerQueryRouter,
                        cancellationToken);
            }

            // Vector tile path. Built-in gridsets render against the standard WebMercatorQuad /
            // WorldCRS84Quad pyramids (gridGeometry stays null so the byte-identical built-in MVT
            // path is used). Operator-defined custom gridsets (#1839) now also serve vector tiles:
            // the resolved gridGeometry is threaded into the MVT provider, which derives the tile
            // envelope and target SRID from the gridset and reprojects the stored geometry into the
            // gridset CRS (ST_Transform).
            var vectorGridGeometry = tileMatrixSetEntry.IsBuiltIn ? null : gridGeometry;

            // Record-but-don't-render the vertical selection (see the documented divergence above):
            // tag it on the trace; the vector query itself is unchanged because there is no Z-aware
            // vector source yet (deferred Shape B).
            if (GetVerticalSelectionForLayer(layer, verticalSelections) is { } vectorVerticalSelection)
            {
                activity?.SetTag("honua.tile.vertical_selection", vectorVerticalSelection.RawValue);
            }

            var (resolvedTileProvider, tileProviderError) = await ResolveTileProviderAsync(
                context, layer, tileProvider, providerQueryRouter, cancellationToken).ConfigureAwait(false);
            if (tileProviderError is not null)
            {
                return tileProviderError;
            }

            return layers.Length == 1
                ? await VectorTileExecution.ExecuteAsync(
                    context,
                    resolvedTileProvider!,
                    layer.StorageLayerId,
                    tileCol,
                    tileRow,
                    zoomLevel,
                    CreateVectorTileQuery(
                        layer.SpatialReferenceSrid,
                        isGeographic,
                        temporalFilter: GetTemporalFilterForLayer(layer, temporalFilters),
                        gridGeometry: vectorGridGeometry),
                    tileOptionsValue,
                    tileLimits,
                    cancellationToken,
                    activity,
                    tileMatrixSetId: tileMatrixSetEntry.Id,
                    gridGeometry: vectorGridGeometry)
                : await ExecuteDatasetVectorTileAsync(
                    context,
                    resolvedTileProvider!,
                    layers,
                    tileCol,
                    tileRow,
                    zoomLevel,
                    isGeographic,
                    GetTemporalFilterForLayer(layer, temporalFilters),
                    tileOptionsValue,
                    tileLimits,
                    activity,
                    cancellationToken,
                    vectorGridGeometry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is the top-level tile request handler boundary; any
        // unanticipated failure must map to a generic 500 rather than crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        RasterTileRenderContext renderContext,
        TemporalFilter? temporalFilter,
        VerticalSelection? verticalSelection,
        FeatureProviderQueryRouter? providerQueryRouter,
        CancellationToken cancellationToken)
    {
        var bounds = renderContext.Bounds;
        var filterSrid = renderContext.FilterSrid;
        var tileLimits = renderContext.TileLimits;
        var tileOptionsValue = renderContext.TileOptions;
        var activity = renderContext.Activity;

        // Record-but-don't-render the vertical selection (see the documented divergence at
        // the raster/vector dispatch). The feature query below is unchanged — there is no
        // Z-aware raster source yet (the Zarr datacube Z-slice render is the deferred
        // Shape B follow-up after #1790).
        if (verticalSelection is { } selection)
        {
            activity?.SetTag("honua.tile.vertical_selection", selection.RawValue);
        }

        var usesRoutedReader = TileFeatureProviderResolver.RequiresRouting(layer.Snapshot, layer.Publication);
        TileBounds queryBounds;
        try
        {
            queryBounds = usesRoutedReader
                ? TransformTileBounds(bounds, filterSrid, layer.SpatialReferenceSrid)
                : bounds;
        }
        catch (NotSupportedException)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Collection '{layer.CollectionId}' cannot render raster tiles from SRID " +
                $"{layer.SpatialReferenceSrid} into tile-matrix SRID {filterSrid}.");
        }

        var querySrid = usesRoutedReader ? layer.SpatialReferenceSrid : filterSrid;
        var spatialFilter = CreateBboxSpatialFilter(queryBounds, querySrid);

        var featureReader = await ResolveFeatureReaderAsync(
            layer, context.RequestServices.GetRequiredService<IFeatureReader>(), providerQueryRouter, cancellationToken)
            .ConfigureAwait(false);
        var featureQuery = new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReferenceSrid,
            OutputSrid = querySrid,
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

        var imageBytes = TileRenderer.RenderTilePng(
            queryResult.Items,
            bounds,
            ToTileGeometryKind(layer.GeometryType),
            sourceSrid: usesRoutedReader ? layer.SpatialReferenceSrid : null,
            targetSrid: usesRoutedReader ? filterSrid : null);

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
        CancellationToken cancellationToken,
        GridGeometry? gridGeometry = null)
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
            cancellationToken,
            gridGeometry);

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
        RasterTileRenderContext renderContext,
        IReadOnlyDictionary<int, TemporalFilter?> temporalFilters,
        FeatureProviderQueryRouter? providerQueryRouter,
        CancellationToken cancellationToken)
    {
        var bounds = renderContext.Bounds;
        var filterSrid = renderContext.FilterSrid;
        var tileLimits = renderContext.TileLimits;
        var tileOptionsValue = renderContext.TileOptions;
        var activity = renderContext.Activity;

        var fallbackFeatureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var renderedLayers = new List<TileRenderer.TileRenderLayer>(layers.Length);
        var remainingBudget = tileLimits.MaxFeaturesPerTile > 0 ? tileLimits.MaxFeaturesPerTile : 10_000;
        var totalFeatureCount = 0;

        foreach (var layer in layers)
        {
            if (remainingBudget <= 0)
            {
                break;
            }

            // Routed per layer (#2962): a multi-collection dataset raster tile request can mix
            // primary- and secondary-provider-backed collections, so each layer's reader must be
            // resolved independently rather than sharing one DI-registered primary reader.
            var usesRoutedReader = TileFeatureProviderResolver.RequiresRouting(layer.Snapshot, layer.Publication);
            TileBounds queryBounds;
            try
            {
                queryBounds = usesRoutedReader
                    ? TransformTileBounds(bounds, filterSrid, layer.SpatialReferenceSrid)
                    : bounds;
            }
            catch (NotSupportedException)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Collection '{layer.CollectionId}' cannot render raster tiles from SRID " +
                    $"{layer.SpatialReferenceSrid} into tile-matrix SRID {filterSrid}.");
            }

            var querySrid = usesRoutedReader ? layer.SpatialReferenceSrid : filterSrid;
            var spatialFilter = CreateBboxSpatialFilter(queryBounds, querySrid);
            var featureReader = await ResolveFeatureReaderAsync(
                layer, fallbackFeatureReader, providerQueryRouter, cancellationToken).ConfigureAwait(false);
            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = layer.SpatialReferenceSrid,
                OutputSrid = querySrid,
                Limit = remainingBudget,
                TemporalFilter = GetTemporalFilterForLayer(layer, temporalFilters)
            };

            var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            renderedLayers.Add(new TileRenderer.TileRenderLayer(
                queryResult.Items,
                ToTileGeometryKind(layer.GeometryType),
                SourceSrid: usesRoutedReader ? layer.SpatialReferenceSrid : null,
                TargetSrid: usesRoutedReader ? filterSrid : null));
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
        CancellationToken cancellationToken,
        GridGeometry? gridGeometry = null)
    {
        List<byte[]>? tileParts = null;
        var totalLength = 0;

        foreach (var layer in layers)
        {
            var query = CreateVectorTileQuery(
                layer.SpatialReferenceSrid,
                isGeographic,
                temporalFilter: temporalFilter,
                gridGeometry: gridGeometry);
            var tileData = await tileProvider.GetMvtTileAsync(
                layer.StorageLayerId,
                tileCol,
                tileRow,
                zoomLevel,
                query,
                tileOptionsValue,
                tileLimits,
                gridGeometry,
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
        TemporalFilter? temporalFilter,
        GridGeometry? gridGeometry = null)
        => VectorTileExecution.CreateQuery(
            spatialReferenceSrid,
            temporalFilter: temporalFilter) with
        {
            // For built-in pyramids the MVT provider keys off OutputSrid (4326 → geographic path,
            // else WebMercator). For custom gridsets (#1839) the provider derives the target SRID
            // from the gridGeometry instead, but we still advertise the gridset SRID here so the
            // filter-transform branch sees the correct storage→gridset reprojection.
            OutputSrid = gridGeometry is not null
                ? gridGeometry.Srid
                : (isGeographic ? 4326 : 3857)
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
            // WKBWriter is not documented as thread-safe; construct per call like
            // the other writer call sites in this slice (antimeridian path only).
            return SpatialFilter.Create(new WKBWriter().Write(multiPolygon), SpatialRelationship.Intersects, srid);
        }

        return SpatialFilterHelpers.CreateBboxSpatialFilter(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax, srid);
    }

    private static TileBounds TransformTileBounds(TileBounds bounds, int fromSrid, int toSrid)
    {
        var transformed = CoordinateTransformer.TransformExtent(
            new RenderExtent(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax),
            fromSrid,
            toSrid);
        return new TileBounds(
            transformed.MinX,
            transformed.MinY,
            transformed.MaxX,
            transformed.MaxY);
    }

    private static IEnumerable<TileSetItem> BuildDatasetTileSetItems(
        string titleBase,
        string baseUrl,
        TileLimits tileLimits,
        string querySuffix,
        bool advertiseVectorTiles,
        ITileMatrixSetRegistry? registry = null)
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

        // Append any operator-defined custom gridsets after the two built-ins (#1916). Default
        // deployments configure none, so the CITE snapshot list output stays byte-identical.
        foreach (var (entry, geometry) in EnumerateCustomGrids(registry, tileLimits))
        {
            yield return BuildCustomTileSetItem(
                $"{titleBase} ({entry.Id})",
                $"{baseUrl}/ogc/tiles/tiles/{entry.Id}{querySuffix}",
                $"{baseUrl}/ogc/tiles/tiles/{entry.Id}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}{querySuffix}",
                $"{baseUrl}/ogc/tiles/tileMatrixSets/{entry.Id}",
                "Dataset tileset metadata",
                entry,
                geometry,
                advertiseVectorTiles);
        }
    }

    private static IEnumerable<TileSetItem> BuildTileSetItems(
        TileRequestLayer layer,
        string baseUrl,
        TileLimits tileLimits,
        string? encodedCollectionId = null,
        ITileMatrixSetRegistry? registry = null)
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

        // Append any operator-defined custom gridsets after the two built-ins (#1916). Default
        // deployments configure none, so the CITE snapshot list output stays byte-identical.
        foreach (var (entry, geometry) in EnumerateCustomGrids(registry, tileLimits))
        {
            var href = $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{entry.Id}";
            yield return BuildCustomTileSetItem(
                $"{layer.DisplayName} ({entry.Id})",
                href,
                $"{href}/{{tileMatrix}}/{{tileRow}}/{{tileCol}}",
                $"{baseUrl}/ogc/tiles/tileMatrixSets/{entry.Id}",
                "Tileset metadata",
                entry,
                geometry,
                advertiseVectorTiles: true);
        }
    }

    // Yields each registered custom (non-built-in) gridset paired with its resolved geometry.
    private static IEnumerable<(TileMatrixSetEntry Entry, GridGeometry? Geometry)> EnumerateCustomGrids(
        ITileMatrixSetRegistry? registry,
        TileLimits tileLimits)
    {
        if (registry is null)
        {
            yield break;
        }

        var maxLevel = Math.Max(0, tileLimits.MaxTileZoom);
        foreach (var entry in registry.All)
        {
            if (entry.IsBuiltIn)
            {
                continue;
            }

            yield return registry.TryGetGeometry(entry.Id, maxLevel, out var geometry)
                ? (entry, geometry)
                : (entry, null);
        }
    }

    private static TileSetItem BuildCustomTileSetItem(
        string title,
        string tilesetHref,
        string tileTemplate,
        string tileMatrixSetHref,
        string selfLinkTitle,
        TileMatrixSetEntry entry,
        GridGeometry? geometry,
        bool advertiseVectorTiles)
    {
        var matrixLimits = geometry is { } resolved
            ? OgcTilesUtilities.BuildTileMatrixSetLimits(resolved)
            : ImmutableArray<TileMatrixSetLimit>.Empty;
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
            Crs = entry.Crs,
            TileMatrixSetId = entry.Id,
            TileMatrixSetUri = entry.Uri,
            TileMatrixSetLimits = matrixLimits,
            Links = links
        };
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
        bool advertiseVectorTiles = true,
        TileMatrixSetEntry? customEntry = null,
        GridGeometry? customGeometry = null)
    {
        // Built-in gridsets (WebMercatorQuad / WorldCRS84Quad) keep the exact static-descriptor +
        // formula-driven limit path so the per-dataset/per-collection tileset documents stay
        // byte-identical for the CITE snapshots. A custom (operator-defined) gridset is advertised
        // from its registered TileMatrixSetEntry (CRS/URI) and its own grid geometry (full-coverage
        // per-level limits) — #1916.
        string crs;
        string uri;
        ImmutableArray<TileMatrixSetLimit> matrixLimits;
        if (customEntry is { IsBuiltIn: false } entry)
        {
            crs = entry.Crs;
            uri = entry.Uri;
            matrixLimits = customGeometry is { } geometry
                ? OgcTilesUtilities.BuildTileMatrixSetLimits(geometry)
                : ImmutableArray<TileMatrixSetLimit>.Empty;
        }
        else
        {
            if (!OgcTileMatrixSetDescriptors.TryGet(tileMatrixSetId, out var descriptor))
            {
                throw new InvalidOperationException($"Unsupported tile matrix set '{tileMatrixSetId}'.");
            }

            crs = descriptor.Crs;
            uri = descriptor.Uri;
            var isGeographic = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId);
            matrixLimits = isGeographic
                ? OgcTilesUtilities.BuildWorldCrs84QuadLimits(tileLimits)
                : BuildTileMatrixSetLimits(tileLimits);
        }

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
            Crs = crs,
            TileMatrixSetId = tileMatrixSetId,
            TileMatrixSetUri = uri,
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
                if (!TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
                {
                    continue;
                }

                // Tiles are a read surface: consult the per-operation resolver for
                // the query grant first (#1376), matching ResolveCollectionLayer so
                // both tile routes enforce the same authorization.
                var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
                    context,
                    resource,
                    service,
                    AuthorizationOperation.Query,
                    cancellationToken).ConfigureAwait(false);

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
            var resolution = await ResolveCollectionLayer(context, snapshot, collectionId, missingCollectionIsBadRequest: true, cancellationToken).ConfigureAwait(false);
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
        foreach (var value in (values).Select(rawValue => ExtractCollectionId(rawValue)))
        {
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

    private static async Task<TileRequestLayerResolution> ResolveCollectionLayer(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        string collectionId,
        bool missingCollectionIsBadRequest,
        CancellationToken cancellationToken)
    {
        var publication = FindCollectionPublication(context, snapshot, collectionId, requireProtocol: true, out var service);
        if (publication is null || service is null)
        {
            var existsWithoutProtocol = FindCollectionPublication(context, snapshot, collectionId, requireProtocol: false, out _) is not null;
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

        // Tiles are a read surface: consult the per-operation resolver for the
        // query grant first (#1376), falling back to the coarse AccessPolicy.
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Query, service, cancellationToken).ConfigureAwait(false);
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
        HttpContext context,
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

            var resource = snapshot.ResolveResource(publication);
            if (!TenantScopeHelpers.IsPublicationVisible(context, publication, resource, candidateService))
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
            service,
            snapshot);
    }

    /// <summary>
    /// Resolves the feature reader used for raster (PNG) tile rendering through
    /// <see cref="TileFeatureProviderResolver"/> (honua-server#2962), so a layer whose
    /// storage binding names a secondary/additional provider connection is rendered from
    /// that provider rather than the DI-registered primary reader.
    /// </summary>
    private static async Task<IFeatureReader> ResolveFeatureReaderAsync(
        TileRequestLayer layer,
        IFeatureReader fallbackReader,
        FeatureProviderQueryRouter? providerQueryRouter,
        CancellationToken cancellationToken)
        => await new TileFeatureProviderResolver(providerQueryRouter).ResolveFeatureReaderAsync(
            layer.Snapshot,
            layer.Service,
            layer.Resource,
            layer.Publication,
            layer.StorageLayerId,
            fallbackReader,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resolves the tile provider used for vector (MVT) tile rendering through
    /// <see cref="TileFeatureProviderResolver"/> (honua-server#2962). When routing resolves to
    /// a provider that does not implement <see cref="ITileProvider"/>, this returns a 501
    /// problem response naming the collection/provider rather than silently falling back to
    /// the DI-registered primary tile provider.
    /// </summary>
    private static async Task<(ITileProvider? Provider, IResult? Error)> ResolveTileProviderAsync(
        HttpContext context,
        TileRequestLayer layer,
        ITileProvider fallbackProvider,
        FeatureProviderQueryRouter? providerQueryRouter,
        CancellationToken cancellationToken)
    {
        var resolution = await new TileFeatureProviderResolver(providerQueryRouter).ResolveTileProviderAsync(
            layer.Snapshot,
            layer.Service,
            layer.Resource,
            layer.Publication,
            layer.StorageLayerId,
            fallbackProvider,
            cancellationToken).ConfigureAwait(false);

        if (resolution.Provider is not null)
        {
            return (resolution.Provider, null);
        }

        return (null, StandardErrorHelpers.CreateNotImplemented(
            context,
            $"Collection '{layer.CollectionId}' is backed by data provider '{resolution.UnsupportedProviderName}', " +
            "which does not support vector tile generation."));
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
        => value.Split(',', StringSplitOptions.None).Any(token => token.Trim().Length == 0);

    private static IResult? ValidateTileQueryParameters(
        HttpContext context,
        TileRequestLayer[] layers,
        string? datetime,
        string? subset,
        string? crs,
        string? subsetCrs,
        TileMatrixSetEntry tileMatrixSetEntry,
        out IReadOnlyDictionary<int, TemporalFilter?> temporalFilters,
        out IReadOnlyDictionary<int, VerticalSelection?> verticalSelections)
    {
        var parsedTemporalFilters = new Dictionary<int, TemporalFilter?>(layers.Length);
        temporalFilters = parsedTemporalFilters;
        var parsedVerticalSelections = new Dictionary<int, VerticalSelection?>(layers.Length);
        verticalSelections = parsedVerticalSelections;

        // A `subset` value is now accepted only when it selects the vertical (elevation)
        // axis — `subset=Z(...)` / `subset=elevation(...)`. Any other axis (or a malformed
        // vertical value) must STILL return 400: this preserves the CITE Tiles 16/16
        // "unknown subset axis is rejected" assertion. The accepted vertical selection is
        // recorded per-layer (parallel to the temporal filters) but is not yet applied to
        // the render — see the documented divergence at the raster/vector dispatch below.
        VerticalSelection? requestedVerticalSelection = null;
        if (!string.IsNullOrWhiteSpace(subset))
        {
            var isVertical = OgcVerticalSelectionParser.TryParseTilesSubset(
                subset,
                out requestedVerticalSelection,
                out var isVerticalAxis,
                out var subsetError);
            if (!isVerticalAxis)
            {
                // Unknown / non-vertical axis (e.g. E(0:1)): unchanged blanket reject.
                return StandardErrorHelpers.CreateBadRequest(context, "The subset parameter is not supported.");
            }

            if (!isVertical)
            {
                // Vertical axis but malformed / out-of-form value: reject with the parser detail.
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    subsetError ?? "Invalid vertical subset value.");
            }
        }

        if (!IsSupportedCrs(crs, tileMatrixSetEntry))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported crs '{crs}'. Only the CRS matching the tile matrix set is supported.");
        }

        if (!IsSupportedCrs(subsetCrs, tileMatrixSetEntry))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported subset-crs '{subsetCrs}'. Only the CRS matching the tile matrix set is supported.");
        }

        var entitlementChecked = false;
        foreach (var layer in layers)
        {
            if (!OgcTemporalFilterParser.TryParse(datetime, layer.Resource, out var temporalFilter, out var errorMessage))
            {
                var detail = layers.Length > 1
                    ? $"Collection '{layer.CollectionId}' cannot satisfy the requested datetime filter. {errorMessage ?? "Invalid datetime parameter."}"
                    : errorMessage ?? "Invalid datetime parameter.";
                return StandardErrorHelpers.CreateBadRequest(context, detail);
            }

            // Time-filtered tiles are a Pro capability. Mirror the GeoServices
            // FeatureServer tile gate (temporal.time-series-tiles), enforced
            // only when the datetime parameter produces an actual filter.
            if (temporalFilter is not null && !entitlementChecked)
            {
                var entitlementError = LicenseGate.RequireEntitlement(
                    context,
                    "temporal.time-series-tiles",
                    "Time-filtered vector tiles");
                if (entitlementError is not null)
                {
                    return entitlementError;
                }

                entitlementChecked = true;
            }

            parsedTemporalFilters[layer.StorageLayerId] = temporalFilter;

            // Record the requested vertical selection for every layer in the request,
            // parallel to the temporal filter. Shape A (#1792): recorded only — no
            // per-layer elevation validation against advertised values exists yet (the
            // datacube vertical extent is the deferred Shape B follow-up after #1790).
            parsedVerticalSelections[layer.StorageLayerId] = requestedVerticalSelection;
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

    private static VerticalSelection? GetVerticalSelectionForLayer(
        TileRequestLayer layer,
        IReadOnlyDictionary<int, VerticalSelection?> verticalSelections)
        => verticalSelections.TryGetValue(layer.StorageLayerId, out var verticalSelection) ? verticalSelection : null;

    private static bool IsSupportedCrs(string? crs, TileMatrixSetEntry tileMatrixSetEntry)
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

        // Built-in gridsets keep their exact accepted-CRS sets so the CITE Tiles assertions and
        // existing snapshot guards stay byte-identical.
        if (tileMatrixSetEntry.IsBuiltIn)
        {
            if (OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetEntry.Id))
            {
                return string.Equals(normalized, OgcTilesUtilities.Crs84, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(normalized, "http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(normalized, "http://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(normalized, OgcTilesUtilities.WebMercatorCrs, StringComparison.OrdinalIgnoreCase);
        }

        // Custom gridset (#1839): tiles are delivered in the gridset CRS, so accept the gridset's
        // own CRS URI or the equivalent EPSG code form.
        if (string.Equals(normalized, tileMatrixSetEntry.Crs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            normalized,
            $"http://www.opengis.net/def/crs/EPSG/0/{tileMatrixSetEntry.Srid}",
            StringComparison.OrdinalIgnoreCase);
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

    private static bool TryRequireTenantContext(HttpContext context, out IResult? error)
    {
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext is not null && tenantContext.RequireTenantId(out var tenantId, out _))
        {
            Activity.Current?.SetTag("honua.tenant_id", tenantId);
            error = null;
            return true;
        }

        error = StandardErrorHelpers.CreateForbidden(
            context,
            "Tenant context is required to access tiles.");
        return false;
    }

    private sealed record TileRequestLayer(
        string CollectionId,
        int PublicLayerId,
        int StorageLayerId,
        string DisplayName,
        string? Description,
        MetadataV2Resource Resource,
        MetadataV2Publication Publication,
        MetadataV2Service Service,
        MetadataV2GraphSnapshot Snapshot)
    {
        public int SpatialReferenceSrid => Resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;

        public MetadataV2GeometryType GeometryType => Resource.ReadGeometryType();
    }

    private readonly record struct TileRequestLayerResolution(TileRequestLayer? Layer, IResult? Error);

}
