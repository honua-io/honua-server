// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcMaps.Handlers;
using Honua.Server.Features.OgcMaps.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.OgcMaps;

/// <summary>
/// OGC API - Maps endpoints providing server-rendered map imagery.
/// Implements OGC API - Maps Part 1: Core specification.
/// </summary>
public static partial class OgcMapsEndpoints
{
    /// <summary>
    /// Maps OGC API - Maps endpoints to the application.
    /// </summary>
    public static void MapOgcMapsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ogc/maps")
            .WithTags("OGC API - Maps");

        // Core conformance endpoint
        group.MapGet("/conformance", GetConformance)
            .WithDisplayName("Maps API Conformance")
            .WithName("GetMapsConformance")
            .WithSummary("Get OGC API - Maps conformance classes")
            .WithDescription("Returns the conformance classes that the server implements from OGC API - Maps standards")
            .Produces<OgcMapsConformance>()
            .CacheOutput("OgcMapsConformance");

        // Collection maps - single collection rendering
        group.MapGet("/collections/{collectionId}/map", GetCollectionMap)
            .WithDisplayName("Get Collection Map")
            .WithName("GetCollectionMap")
            .WithSummary("Render map from a single collection")
            .WithDescription("Returns a rendered map image from the specified collection with optional styling and spatial subsetting")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404);

        // Map TileSets - integration with OGC API - Tiles
        group.MapGet("/collections/{collectionId}/map/tiles", GetCollectionMapTileSets)
            .WithDisplayName("Get Collection Map TileSets")
            .WithName("GetCollectionMapTileSets")
            .WithSummary("Get available map tile sets for a collection")
            .WithDescription("Returns the tile set metadata for maps generated from the specified collection")
            .Produces<TileSet[]>()
            .Produces(404);

        // Dataset-wide maps - multiple collections
        group.MapGet("/map", GetDatasetMap)
            .WithDisplayName("Get Dataset Map")
            .WithName("GetDatasetMap")
            .WithSummary("Render map from one or more collections")
            .WithDescription("Returns a rendered map image from explicitly selected collections or, when omitted, all accessible collections")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404);

        // Styled maps - collection with specific style
        group.MapGet("/collections/{collectionId}/styles/{styleId}/map", GetStyledMap)
            .WithDisplayName("Get Styled Map")
            .WithName("GetStyledMap")
            .WithSummary("Render styled map from a collection")
            .WithDescription("Returns a rendered map image from the specified collection using a specific style definition")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(501)
            .Produces(404);
    }

    /// <summary>
    /// Get OGC API - Maps conformance classes.
    /// </summary>
    private static async Task<IResult> GetConformance(
        OgcMapsConformanceHandler handler,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.GetConformanceAsync(cancellationToken);
        return Results.Ok(result);
    }

    /// <summary>
    /// Get rendered map from a single collection.
    /// </summary>
    private static async Task<IResult> GetCollectionMap(
        string collectionId,
        [AsParameters] OgcMapRequest request,
        HttpContext context,
        OgcMapsRenderingHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseNonNegativeCollectionId(collectionId, out var layerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Collection ID must be a valid integer");
        }

        return await handler.RenderCollectionMapAsync(layerId, request, context: context, cancellationToken);
    }

    /// <summary>
    /// Get rendered map from multiple collections (dataset-wide).
    /// </summary>
    private static async Task<IResult> GetDatasetMap(
        [AsParameters] OgcMapRequest request,
        HttpContext context,
        OgcMapsRenderingHandler handler,
        CancellationToken cancellationToken = default)
    {
        var selectedLayerIds = Array.Empty<int>();
        if (request.Collections is not null)
        {
            if (HasEmptyCommaSeparatedToken(request.Collections))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Valid collection IDs are required");
            }

            var collectionTokens = request.Collections.Split(',', StringSplitOptions.TrimEntries);
            if (collectionTokens.Length == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Valid collection IDs are required");
            }

            if (collectionTokens.Length > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be requested at once.");
            }

            var collectionIds = new List<int>(collectionTokens.Length);
            var invalidCollections = new List<string>();
            foreach (var token in collectionTokens)
            {
                if (TryParseNonNegativeCollectionId(token, out var layerId))
                {
                    collectionIds.Add(layerId);
                }
                else
                {
                    invalidCollections.Add(token);
                }
            }

            if (invalidCollections.Count > 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Invalid collection IDs: {string.Join(", ", invalidCollections)}");
            }

            if (collectionIds.Count == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Valid collection IDs are required");
            }

            selectedLayerIds = collectionIds.Distinct().ToArray();
            if (selectedLayerIds.Length > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be requested at once.");
            }
        }

        return await handler.RenderDatasetMapAsync(selectedLayerIds, request, context: context, cancellationToken);
    }

    /// <summary>
    /// Get rendered map with a specific style applied.
    /// </summary>
    private static async Task<IResult> GetStyledMap(
        string collectionId,
        string styleId,
        [AsParameters] OgcMapRequest request,
        HttpContext context,
        OgcMapsRenderingHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseNonNegativeCollectionId(collectionId, out var layerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Collection ID must be a valid integer");
        }

        // Validate styleId from URL path: alphanumeric, dash, underscore only
        if (string.IsNullOrEmpty(styleId) || !StyleIdPattern().IsMatch(styleId) || styleId.Length > 100)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "StyleId must contain only alphanumeric characters, dashes, and underscores");
        }

        return await handler.RenderStyledMapAsync(layerId, styleId, request, context: context, cancellationToken);
    }

    /// <summary>
    /// Get map tile sets for a collection.
    /// </summary>
    private static async Task<IResult> GetCollectionMapTileSets(
        string collectionId,
        HttpContext context,
        OgcMapsTileSetHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseNonNegativeCollectionId(collectionId, out var layerId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Collection ID must be a valid integer");
        }

        return await handler.GetMapTileSetsAsync(layerId, context: context, cancellationToken);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex StyleIdPattern();

    private static bool TryParseNonNegativeCollectionId(string value, out int layerId)
    {
        var parsed = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId);
        return parsed && layerId >= 0;
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
}
