// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Coverages.Handlers;
using Honua.Protocols.Ogc.Api.Coverages.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Coverages;

/// <summary>
/// OGC API - Coverages endpoints for raster coverage discovery and retrieval.
/// </summary>
public static class OgcCoveragesEndpoints
{
    /// <summary>
    /// Maps OGC API - Coverages endpoints to the application.
    /// </summary>
    public static void MapOgcCoveragesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/ogc/coverages")
            .WithTags("OGC API - Coverages");

        group.MapGet(string.Empty, GetLandingPage)
            .WithDisplayName("Coverages API Landing Page")
            .WithName("GetCoveragesLandingPage")
            .WithSummary("Get OGC API - Coverages landing page")
            .WithDescription("The landing page provides links to the OGC API - Coverages definition and resources.")
            .Produces<LandingPage>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .CacheOutput("OgcCoveragesLandingPage");

        group.MapGet("/conformance", GetConformance)
            .WithDisplayName("Coverages API Conformance")
            .WithName("GetCoveragesConformance")
            .WithSummary("Get OGC API - Coverages conformance classes")
            .WithDescription("Returns the conformance classes implemented by the OGC API - Coverages surface.")
            .Produces<ConformanceDeclaration>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .CacheOutput("OgcCoveragesConformance");

        group.MapGet("/api", GetOpenApiSpec)
            .WithDisplayName("Coverages API OpenAPI Specification Alias")
            .WithName("GetCoveragesOpenApiSpecAlias")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API - Coverages")
            .WithDescription("The OpenAPI specification describes all available OGC API - Coverages endpoints.")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.OpenApi)
            .Produces(StatusCodes.Status404NotFound)
            .CacheOutput("OgcCoveragesOpenApi");

        group.MapGet("/openapi.json", GetOpenApiSpec)
            .WithDisplayName("Coverages API OpenAPI Specification")
            .WithName("GetCoveragesOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API - Coverages")
            .WithDescription("The OpenAPI specification describes all available OGC API - Coverages endpoints.")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.OpenApi)
            .Produces(StatusCodes.Status404NotFound)
            .CacheOutput("OgcCoveragesOpenApi");

        group.MapGet("/collections", GetCollections)
            .WithDisplayName("Coverages API Collections")
            .WithName("GetCoverageCollections")
            .WithSummary("List OGC API - Coverages collections")
            .WithDescription("Lists accessible raster coverage collections.")
            .Produces<OgcCoverageCollections>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        group.MapGet("/collections/{collectionId}", GetCollection)
            .WithDisplayName("Coverages API Collection")
            .WithName("GetCoverageCollection")
            .WithSummary("Get OGC API - Coverages collection metadata")
            .WithDescription("Returns metadata for one raster coverage collection.")
            .Produces<OgcCoverageCollection>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        group.MapGet("/collections/{collectionId}/schema", GetSchema)
            .WithDisplayName("Coverages API Collection Schema")
            .WithName("GetCoverageCollectionSchema")
            .WithSummary("Get field-selection schema for an OGC API - Coverages collection")
            .WithDescription("Returns a JSON Schema document describing selectable coverage bands.")
            .Produces<CoverageSchema>(StatusCodes.Status200OK, MediaTypes.SchemaJson)
            .Produces<CoverageSchema>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        group.MapGet("/collections/{collectionId}/coverage", GetCoverage)
            .WithDisplayName("Coverages API Collection Coverage")
            .WithName("GetCoverageCollectionCoverage")
            .WithSummary("Get coverage data for an OGC API - Coverages collection")
            .WithDescription("Returns the selected coverage as GeoTIFF by default or PNG when negotiated.")
            .Produces(StatusCodes.Status200OK, contentType: GeoTiffContentType)
            .Produces(StatusCodes.Status200OK, contentType: MediaTypes.Png)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status406NotAcceptable)
            .Produces(StatusCodes.Status500InternalServerError);
    }

    private const string GeoTiffContentType = "image/tiff";

    private static IResult GetLandingPage(
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetLandingPage(context, f);

    private static IResult GetConformance(
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetConformance(context, f);

    private static Task<IResult> GetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment,
        OgcCoveragesHandler handler)
        => handler.GetOpenApiSpecAsync(context, f, environment);

    private static Task<IResult> GetCollections(
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetCollectionsAsync(context, f, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetCollection(
        string collectionId,
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetCollectionAsync(context, collectionId, f, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetSchema(
        string collectionId,
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetSchemaAsync(context, collectionId, f, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetCoverage(
        string collectionId,
        HttpContext context,
        string? f,
        OgcCoveragesHandler handler)
        => handler.GetCoverageAsync(context, collectionId, f, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));
}
