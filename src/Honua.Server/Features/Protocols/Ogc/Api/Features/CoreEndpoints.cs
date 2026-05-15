// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Core metadata endpoints for OGC API Features (landing page, conformance, OpenAPI)
/// </summary>
internal static class CoreEndpoints
{
    private static readonly TimeSpan _landingPageCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _conformanceCacheDuration = TimeSpan.FromHours(1);
    private const string GmlApplicationSchema = """
<?xml version="1.0" encoding="UTF-8"?>
<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
           xmlns:gml="http://www.opengis.net/gml/3.2"
           xmlns:app="https://honua.io/gml/ogcapi-features/1.0"
           targetNamespace="https://honua.io/gml/ogcapi-features/1.0"
           elementFormDefault="qualified"
           attributeFormDefault="unqualified"
           version="1.0">
  <xs:import namespace="http://www.opengis.net/gml/3.2"
             schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd" />
  <xs:element name="Feature" type="app:FeatureType" substitutionGroup="gml:AbstractFeature" />
  <xs:complexType name="FeatureType">
    <xs:complexContent>
      <xs:extension base="gml:AbstractFeatureType">
        <xs:sequence>
          <xs:element name="geometry" type="gml:GeometryPropertyType" minOccurs="0" maxOccurs="1" />
          <xs:element name="property" type="app:PropertyType" minOccurs="0" maxOccurs="unbounded" />
        </xs:sequence>
      </xs:extension>
    </xs:complexContent>
  </xs:complexType>
  <xs:complexType name="PropertyType" mixed="true">
    <xs:attribute name="name" type="xs:string" use="required" />
  </xs:complexType>
</xs:schema>
""";

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

        // OGC clients commonly expect the API definition at /ogc/features/api
        var apiAlias = endpoints.MapGet("/ogc/features/api", HandleGetOpenApiSpec)
            .WithDisplayName("OGC API Features API Definition")
            .WithName("OgcApiDefinition")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Features")
            .WithDescription("Alias for the OpenAPI specification at /openapi.json")
            .WithTags("OGC API Features")
            .CacheOutput("OgcOpenApi")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

        _ = endpoints.MapGet(OgcFeaturesUtilities.GmlApplicationSchemaPath, HandleGetGmlApplicationSchema)
            .WithDisplayName("OGC API Features GML Application Schema")
            .WithName("OgcFeaturesGmlApplicationSchema")
            .WithSummary("Get the GML application schema for OGC API Features GML responses")
            .WithDescription("The schema declares Honua's feature member and property elements used by GML feature collection responses.")
            .WithTags("OGC API Features")
            .CacheOutput("OgcGmlApplicationSchema")
            .Produces<string>(200, "application/xml");

        return endpoints;
    }

    private static IResult HandleGetGmlApplicationSchema()
        => Results.Text(GmlApplicationSchema, "application/xml");

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static IResult HandleGetLandingPage(
        HttpContext context,
        string? f,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        OgcFeaturesLog.LandingPageRequested(logger);

        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                OgcFeaturesUtilities.AllowedQueryParameters.Metadata,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/features";
        EnsureCacheControl(context, _landingPageCacheDuration);

        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);

        // API definition
        links.Add(Link.Create(
            href: $"{baseUrl}/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        var serveApiDocs = configuration.GetValue(
            "ServeApiDocs",
            configuration.GetValue("HONUA_SERVE_API_DOCS", false));
        if (serveApiDocs)
        {
            links.Add(Link.Create(
                href: $"{baseUrl}/docs",
                rel: RelationTypes.ServiceDoc,
                type: MediaTypes.Html,
                title: "API documentation"));
        }

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

        // Dataset map representation
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/maps/map",
            rel: RelationTypes.Map,
            type: "image/png",
            title: "Dataset map"));

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

        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                OgcFeaturesUtilities.AllowedQueryParameters.Metadata,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
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
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/sorting",
                "http://www.opengis.net/spec/cql2/1.0/conf/cql2-text",
                "http://www.opengis.net/spec/cql2/1.0/conf/cql2-json",
                "http://www.opengis.net/spec/cql2/1.0/conf/basic-cql2",
                "http://www.opengis.net/spec/cql2/1.0/conf/basic-spatial-operators",
                // CQL2 classes the parser already implements. Declaring them unlocks
                // the matching OGC conformance tests and lets compliant clients exercise
                // the operators (previously they were working but un-advertised).
                "http://www.opengis.net/spec/cql2/1.0/conf/advanced-comparison-operators",
                "http://www.opengis.net/spec/cql2/1.0/conf/case-insensitive-comparison",
                "http://www.opengis.net/spec/cql2/1.0/conf/accent-insensitive-comparison",
                "http://www.opengis.net/spec/cql2/1.0/conf/temporal",
                "http://www.opengis.net/spec/cql2/1.0/conf/array-operators",

                // OGC API Features Part 4 - Create, Replace, Delete
                "http://www.opengis.net/spec/ogcapi-features-4/1.0/conf/create-replace-delete"
            ).AddRange(OgcConformanceUris.Common)
              .AddRange(OgcConformanceUris.VendorExtensions),
            Links = OgcCoreMetadataUtilities.BuildConformanceLinks(
                context,
                $"{baseUrl}/ogc/features/conformance",
                outputFormat)
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
        return await OgcCoreMetadataUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OgcFeaturesUtilities.AllowedQueryParameters.OpenApi,
            "openapi.json",
            fallbackSpec);
    }
}
