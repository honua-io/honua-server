// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Honua.Server.Features.OgcFeatures.Models;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Core metadata endpoints for OGC API Features (landing page, conformance, OpenAPI)
/// </summary>
internal static class CoreEndpoints
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _openApiCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps core metadata endpoints for OGC API Features
    /// </summary>
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features", HandleGetLandingPage)
            .WithDisplayName("OGC API Features Landing Page")
            .WithName("OgcLandingPage")
            .WithSummary("Get OGC API Features landing page")
            .WithDescription("The landing page provides links to the API definition and other resources")
            .WithTags("OGC API Features")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/openapi.json", HandleGetOpenApiSpec)
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
    private static IResult HandleGetLandingPage(HttpContext context, string? f)
    {
        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features";

        var links = OgcFeaturesUtilities.BuildFormatLinks(request, basePath, outputFormat, OgcFeaturesUtilities.MetadataFormats, "This document")
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

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Links = links.ToImmutable()
        };

        return OgcFeaturesUtilities.FormatMetadataResponse(landingPage, OgcJsonContext.Default.LandingPage, outputFormat, "Landing page");
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static IResult HandleGetConformance(HttpContext context, string? f)
    {
        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                // OGC API Features Core
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

                // OGC API Features CRS
                "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs",

                // OGC API Features Filtering
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables-query-parameters",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter-cql2-text",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter-cql2-json",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/features-filter",

                // OGC API Common
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            ),
            Links = OgcFeaturesUtilities.BuildFormatLinks(
                context.Request,
                $"{context.Request.Scheme}://{context.Request.Host}/ogc/features/conformance",
                outputFormat,
                OgcFeaturesUtilities.MetadataFormats,
                "Conformance declaration")
        };

        return OgcFeaturesUtilities.FormatMetadataResponse(conformance, OgcJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    /// <summary>
    /// Handles the OpenAPI 3.0 specification request using pre-generated static content for AOT compatibility
    /// </summary>
    private static async Task<IResult> HandleGetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        var request = context.Request;

        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcFeaturesUtilities.AllowedQueryParameters.OpenApi);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "json", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest($"Unsupported format '{f}'");
        }

        var acceptHeader = request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader) &&
            !acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/vnd.oai.openapi+json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        // Serve pre-generated OpenAPI spec for AOT compatibility
        // This avoids using Dictionary<string, object?> which is problematic for AOT compilation
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
        return Results.Content(fallbackSpec, MediaTypes.OpenApi);
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
        var openApiPath = Path.Combine(contentRootPath, "openapi.json");
        if (!File.Exists(openApiPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(openApiPath);
    }
}
