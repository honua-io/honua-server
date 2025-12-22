// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Extension methods to register OGC API Features endpoints
/// </summary>
public static class OgcFeaturesEndpoints
{
    /// <summary>
    /// Maps OGC API Features endpoints for Core and Conformance
    /// </summary>
    public static IEndpointRouteBuilder MapOgcFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features", HandleGetLandingPage)
            .WithDisplayName("OGC API Features Landing Page")
            .WithName("OgcLandingPage")
            .WithSummary("Get OGC API Features landing page")
            .WithDescription("The landing page provides links to the API definition and other resources")
            .WithTags("OGC API Features")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static Ok<LandingPage> HandleGetLandingPage(HttpContext context)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Links = ImmutableArray.Create(
                // Self link
                Link.Create(
                    href: $"{baseUrl}/ogc/features",
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: "This document"
                ),

                // API definition
                Link.Create(
                    href: $"{baseUrl}/openapi.json",
                    rel: RelationTypes.ServiceDesc,
                    type: MediaTypes.OpenApi,
                    title: "API definition"
                ),

                // Conformance declaration
                Link.Create(
                    href: $"{baseUrl}/ogc/features/conformance",
                    rel: RelationTypes.Conformance,
                    type: MediaTypes.Json,
                    title: "Conformance declaration"
                ),

                // Collections (will be implemented in issue #16)
                Link.Create(
                    href: $"{baseUrl}/ogc/features/collections",
                    rel: RelationTypes.Data,
                    type: MediaTypes.Json,
                    title: "Feature collections"
                )
            )
        };

        return TypedResults.Ok(landingPage);
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static Ok<ConformanceDeclaration> HandleGetConformance()
    {
        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                // OGC API Features Core
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

                // OGC API Common
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            )
        };

        return TypedResults.Ok(conformance);
    }
}
