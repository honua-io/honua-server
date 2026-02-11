// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Core metadata endpoints for OGC API Features (landing page, conformance, OpenAPI)
/// </summary>
internal static class CoreEndpoints
{
    private static readonly TimeSpan _landingPageCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _conformanceCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Maps core metadata endpoints for OGC API Features
    /// </summary>
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var landing = endpoints.MapGet("/ogc/features", HandleGetLandingPage)
            .WithDisplayName("OGC API Features Landing Page")
            .WithName("OgcLandingPage")
            .WithSummary("Get OGC API Features landing page")
            .WithDescription("The landing page provides links to the API definition and other resources")
            .WithTags("OGC API Features")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        var conformance = endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        var openApi = endpoints.MapGet("/openapi.json", HandleGetOpenApiSpec)
            .WithDisplayName("OGC API Features OpenAPI Specification")
            .WithName("OpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Features")
            .WithDescription("The OpenAPI specification describes all available endpoints and their parameters")
            .WithTags("OGC API Features")
            .CacheOutput("OgcOpenApi")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static IResult HandleGetLandingPage(
        HttpContext context,
        string? f,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        OgcFeaturesLog.LandingPageRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return OgcCommonUtilities.CreateFormatError(context, formatError);
        }

        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/features";
        EnsureCacheControl(context, _landingPageCacheDuration);

        var links = OgcCommonUtilities.BuildFormatLinks(request, basePath, outputFormat, OgcCommonUtilities.MetadataFormats, "This document")
            .ToBuilder();

        // API definition
        links.Add(Link.Create(
            href: $"{baseUrl}/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        // Conformance declaration
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));

        // Collections
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections",
            rel: RelationTypes.Data,
            type: MediaTypes.Json,
            title: "Feature collections"));

        // Vector tilesets list
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles/tiles",
            rel: RelationTypes.TilesetsVector,
            type: MediaTypes.Json,
            title: "Vector tilesets"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Supports3d = false,
            Links = links.ToImmutable()
        };

        OgcFeaturesLog.LandingPageReturned(logger);
        return OgcCommonUtilities.FormatMetadataResponse(landingPage, OgcJsonContext.Default.LandingPage, outputFormat, "Landing page");
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static IResult HandleGetConformance(
        HttpContext context,
        string? f,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        OgcFeaturesLog.ConformanceRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return OgcCommonUtilities.CreateFormatError(context, formatError);
        }

        EnsureCacheControl(context, _conformanceCacheDuration);
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                // OGC API Features Part 1 - Core
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

                // OGC API Features Part 2 - Coordinate Reference Systems by Reference
                "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs",

                // OGC API Features Part 3 - Filtering
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables"
            ).AddRange(OgcConformanceUris.Common),
            Links = OgcCommonUtilities.BuildFormatLinks(
                context.Request,
                $"{baseUrl}/ogc/features/conformance",
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "Conformance declaration")
        };

        OgcFeaturesLog.ConformanceReturned(logger, conformance.ConformsTo.Length);
        return OgcCommonUtilities.FormatMetadataResponse(conformance, OgcJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    private static void EnsureCacheControl(HttpContext context, TimeSpan maxAge)
    {
        if (context.Response.Headers.ContainsKey("Cache-Control"))
        {
            return;
        }

        var maxAgeSeconds = (int)Math.Max(0, maxAge.TotalSeconds);
        context.Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
    }

    /// <summary>
    /// Handles the OpenAPI 3.0 specification request using pre-generated static content for AOT compatibility
    /// </summary>
    private static async Task<IResult> HandleGetOpenApiSpec(
        HttpContext context,
        string? f,
        [FromServices] IWebHostEnvironment environment)
    {
        // Fallback: serve minimal spec if file not found
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Features",
            "description": "OGC API Features implementation for geospatial data access",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;
        return await OgcOpenApiSpecUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OgcFeaturesUtilities.AllowedQueryParameters.OpenApi,
            "openapi.json",
            fallbackSpec);
    }
}
