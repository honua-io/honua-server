// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.Protocols.Ogc.Api.Maps.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.Ogc.Api.Maps;

/// <summary>
/// OGC API - Maps endpoints providing server-rendered map imagery.
/// Implements OGC API - Maps Part 1: Core specification.
/// </summary>
public static partial class OgcMapsEndpoints
{
    private static readonly ImmutableHashSet<string> MetadataQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    private static readonly ImmutableHashSet<string> OpenApiQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    /// <summary>
    /// Maps OGC API - Maps endpoints to the application.
    /// </summary>
    public static void MapOgcMapsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ogc/maps")
            .WithTags("OGC API - Maps");

        group.MapGet(string.Empty, GetLandingPage)
            .WithDisplayName("Maps API Landing Page")
            .WithName("GetMapsLandingPage")
            .WithSummary("Get OGC API - Maps landing page")
            .WithDescription("The landing page provides links to the OGC API - Maps definition and related resources")
            .Produces<LandingPage>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .CacheOutput("OgcMapsLandingPage");

        // Core conformance endpoint
        group.MapGet("/conformance", GetConformance)
            .WithDisplayName("Maps API Conformance")
            .WithName("GetMapsConformance")
            .WithSummary("Get OGC API - Maps conformance classes")
            .WithDescription("Returns the conformance classes that the server implements from OGC API - Maps standards")
            .Produces<OgcMapsConformance>()
            .CacheOutput("OgcMapsConformance");

        group.MapGet("/openapi.json", GetOpenApiSpec)
            .WithDisplayName("Maps API OpenAPI Specification")
            .WithName("GetMapsOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API - Maps")
            .WithDescription("The OpenAPI specification describes all available OGC API - Maps endpoints")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.OpenApi)
            .Produces(StatusCodes.Status404NotFound)
            .CacheOutput("OgcMapsOpenApi");

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

        group.MapGet("/collections/{collectionId}/styles/{styleId}/map", GetStyledCollectionMap)
            .WithDisplayName("Get Styled Collection Map")
            .WithName("GetStyledCollectionMap")
            .WithSummary("Render a styled map from a single collection")
            .WithDescription("Returns a rendered map image from the specified collection with the requested style applied")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        // Map TileSets - integration with OGC API - Tiles
        group.MapGet("/collections/{collectionId}/map/tiles", GetCollectionMapTileSets)
            .WithDisplayName("Get Collection Map TileSets")
            .WithName("GetCollectionMapTileSets")
            .WithSummary("Get available map tile sets for a collection")
            .WithDescription("Returns the tile set metadata for maps generated from the specified collection")
            .Produces<TileSetsList>()
            .Produces(404);

        group.MapGet("/collections/{collectionId}/map/tiles/{tileMatrixSetId}", GetCollectionMapTileSet)
            .WithDisplayName("Get Collection Map TileSet")
            .WithName("GetCollectionMapTileSet")
            .WithSummary("Get map tile set metadata for a collection and tile matrix set")
            .WithDescription("Returns the tile set metadata for maps generated from the specified collection and tile matrix set")
            .Produces<TileSet>()
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

    }

    /// <summary>
    /// Get OGC API - Maps landing page.
    /// </summary>
    private static IResult GetLandingPage(HttpContext context, string? f)
    {
        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/maps";
        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/maps/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/maps/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/maps/map",
            rel: RelationTypes.Map,
            type: "image/png",
            title: "Dataset map"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Maps",
            Description = "OGC API Maps implementation for server-rendered imagery",
            Supports3d = false,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            landingPage,
            OgcMapsJsonContext.Default.LandingPage,
            outputFormat,
            "Landing page");
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

    private static async Task<IResult> GetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Maps",
            "description": "OGC API Maps implementation for server-rendered imagery",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;

        return await OgcCoreMetadataUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OpenApiQueryParameters,
            "ogc-maps-openapi.json",
            fallbackSpec);
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
        var layerId = await ResolveCollectionLayerIdAsync(context, collectionId, cancellationToken);
        if (!layerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        return await handler.RenderCollectionMapAsync(layerId.Value, request, context: context, cancellationToken);
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
                var layerId = await ResolveCollectionLayerIdAsync(context, token, cancellationToken);
                if (layerId.HasValue)
                {
                    collectionIds.Add(layerId.Value);
                }
                else
                {
                    invalidCollections.Add(token);
                }
            }

            if (invalidCollections.Count > 0)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Collections not found: {string.Join(", ", invalidCollections)}");
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
    /// Get rendered map from a single collection with a named style.
    /// </summary>
    private static async Task<IResult> GetStyledCollectionMap(
        string collectionId,
        string styleId,
        [AsParameters] OgcMapRequest request,
        HttpContext context,
        OgcMapsRenderingHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerId = await ResolveCollectionLayerIdAsync(context, collectionId, cancellationToken);
        if (!layerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        return await handler.RenderStyledMapAsync(layerId.Value, styleId, request, context: context, cancellationToken);
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
        var layerId = await ResolveCollectionLayerIdAsync(context, collectionId, cancellationToken);
        if (!layerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        return await handler.GetMapTileSetsAsync(layerId.Value, context: context, cancellationToken);
    }

    private static async Task<IResult> GetCollectionMapTileSet(
        string collectionId,
        string tileMatrixSetId,
        HttpContext context,
        OgcMapsTileSetHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerId = await ResolveCollectionLayerIdAsync(context, collectionId, cancellationToken);
        if (!layerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }

        return await handler.GetMapTileSetAsync(layerId.Value, tileMatrixSetId, context: context, cancellationToken);
    }

    private static async Task<int?> ResolveCollectionLayerIdAsync(
        HttpContext context,
        string value,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await resourceValidator.ValidateCollectionV2Async(value, cancellationToken);
        if (!validationResult.IsValid || validationResult.Resource is null)
        {
            return null;
        }

        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.ResolveStorageLayerId(validationResult.Resource);
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
