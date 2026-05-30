// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static class CoreEndpoints
{
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var landing = endpoints.MapGet("/ogc/tiles", HandleGetLandingPage)
            .WithDisplayName("OGC API Tiles Landing Page")
            .WithName("OgcTilesLandingPage")
            .WithSummary("Get OGC API Tiles landing page")
            .WithDescription("Landing page for the OGC API Tiles endpoint set")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var conformance = endpoints.MapGet("/ogc/tiles/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Tiles Conformance")
            .WithName("OgcTilesConformance")
            .WithSummary("Get OGC API Tiles conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var openApi = endpoints.MapGet("/ogc/tiles/openapi.json", HandleGetOpenApiSpec)
            .WithDisplayName("OGC API Tiles OpenAPI Specification")
            .WithName("OgcTilesOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Tiles")
            .WithDescription("The OpenAPI specification describes all available OGC Tiles endpoints")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesOpenApi")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

        return endpoints;
    }

    private static IResult HandleGetLandingPage(HttpContext context, string? f)
    {
        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                OgcTilesUtilities.AllowedQueryParameters.Metadata,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/tiles";
        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/collections",
            rel: RelationTypes.Data,
            type: MediaTypes.Json,
            title: "Collections"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/tiles",
            rel: RelationTypes.TilesetsVector,
            type: MediaTypes.Json,
            title: "Vector tilesets"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/tileMatrixSets",
            rel: "http://www.opengis.net/def/rel/ogc/1.0/tiling-schemes",
            type: MediaTypes.Json,
            title: "Tile matrix sets"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Tiles",
            Description = "OGC API Tiles implementation for vector tiles",
            Supports3d = false,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(landingPage, OgcTilesJsonContext.Default.LandingPage, outputFormat, "Landing page");
    }

    private static IResult HandleGetConformance(HttpContext context, string? f)
    {
        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                OgcTilesUtilities.AllowedQueryParameters.Metadata,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tileset",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tilesets-list",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/dataset-tilesets",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/geodata-tilesets",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/mvt",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/png",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/oas30"
            ).AddRange(OgcConformanceUris.Common),
            Links = OgcCoreMetadataUtilities.BuildConformanceLinks(
                context,
                $"{baseUrl}/ogc/tiles/conformance",
                outputFormat)
        };

        return OgcCommonUtilities.FormatMetadataResponse(conformance, OgcTilesJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    private static async Task<IResult> HandleGetOpenApiSpec(
        HttpContext context,
        string? f,
        [FromServices] IWebHostEnvironment environment)
    {
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Tiles",
            "description": "OGC API Tiles implementation for vector tiles",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;
        return await OgcCoreMetadataUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OgcTilesUtilities.AllowedQueryParameters.OpenApi,
            "ogc-tiles-openapi.json",
            fallbackSpec);
    }
}
