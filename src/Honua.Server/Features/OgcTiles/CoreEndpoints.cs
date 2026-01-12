// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcTiles;

internal static class CoreEndpoints
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _openApiCache =
        new(StringComparer.OrdinalIgnoreCase);

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
        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/tiles";

        var links = OgcCommonUtilities.BuildFormatLinks(request, basePath, outputFormat, OgcCommonUtilities.MetadataFormats, "This document")
            .ToBuilder();

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
        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tileset",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tilesets-list",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/dataset-tilesets",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/geodata-tilesets",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/mvt",
                "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            ),
            Links = OgcCommonUtilities.BuildFormatLinks(
                context.Request,
                $"{context.Request.Scheme}://{context.Request.Host}/ogc/tiles/conformance",
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Conformance declaration")
        };

        return OgcCommonUtilities.FormatMetadataResponse(conformance, OgcTilesJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    private static async Task<IResult> HandleGetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        var request = context.Request;
        var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.OpenApi);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "json", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Unsupported format '{f}'");
        }

        var acceptHeader = request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader) &&
            !acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/vnd.oai.openapi+json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                StatusCodes.Status406NotAcceptable,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        string? openApiContent = null;
        try
        {
            openApiContent = await GetOpenApiContentAsync(environment.ContentRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            openApiContent = null;
        }

        if (!string.IsNullOrWhiteSpace(openApiContent))
        {
            return Results.Content(openApiContent, MediaTypes.OpenApi);
        }

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
        return Results.Content(fallbackSpec, MediaTypes.OpenApi);
    }

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return StandardErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return StandardErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }

    private static Task<string?> GetOpenApiContentAsync(string contentRootPath)
    {
        var rootPath = Path.GetFullPath(contentRootPath);
        var cacheEntry = _openApiCache.GetOrAdd(rootPath, _ => new Lazy<Task<string?>>(
            () => ReadOpenApiContentAsync(rootPath)));
        return cacheEntry.Value;
    }

    private static async Task<string?> ReadOpenApiContentAsync(string contentRootPath)
    {
        var openApiPath = Path.Combine(contentRootPath, "ogc-tiles-openapi.json");
        if (!File.Exists(openApiPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(openApiPath);
    }
}
