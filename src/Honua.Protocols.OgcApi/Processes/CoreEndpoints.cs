// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// OGC API Processes core metadata endpoints (landing page, conformance).
/// </summary>
internal static class CoreEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    private static readonly HashSet<string> MetadataQueryParameters = new(StringComparer.OrdinalIgnoreCase) { "f" };
    private static readonly HashSet<string> OpenApiQueryParameters = new(StringComparer.OrdinalIgnoreCase) { "f" };

    private static readonly ImmutableArray<string> ConformanceClasses = ImmutableArray.Create(
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json",
        // conf/job-list intentionally omitted: the V1 /jobs endpoint lists jobs
        // but does not yet implement the full conf/job-list requirements
        // (filtering, paging semantics, links shape). Declare it only when
        // conf/dismiss is also intentionally omitted: DELETE is available for
        // best-effort cancellation of in-flight jobs, but V1 does not yet
        // satisfy the full dismiss requirements for finished-job cleanup.
        // the implementation fully matches the class.
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json");

    public static void MapOgcProcessesCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(BasePath, GetLandingPage)
            .WithTags(Tag)
            .WithName("OgcProcessesLandingPage")
            .WithSummary("OGC API Processes landing page")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>()
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/conformance", GetConformance)
            .WithTags(Tag)
            .WithName("OgcProcessesConformance")
            .WithSummary("OGC API Processes conformance declaration")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>()
            .Produces<string>(StatusCodes.Status200OK, MediaTypes.Html)
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/openapi.json", GetOpenApiSpec)
            .WithTags(Tag)
            .WithName("OgcProcessesOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Processes")
            .CacheOutput("OgcOpenApi")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.OpenApi)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static IResult GetLandingPage(
        HttpContext context,
        string? f,
        ILogger<OgcProcessesEndpointsLog> logger)
    {
        OgcProcessesLog.LandingPageRequested(logger);

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
        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, BasePath, outputFormat);
        links.Add(Link.Create($"{baseUrl}{BasePath}/openapi.json", RelationTypes.ServiceDesc, MediaTypes.OpenApi, "API definition"));
        links.Add(Link.Create($"{baseUrl}{BasePath}/conformance", RelationTypes.Conformance, MediaTypes.Json, "Conformance declaration"));
        links.Add(Link.Create($"{baseUrl}{BasePath}/processes", "http://www.opengis.net/def/rel/ogc/1.0/processes", MediaTypes.Json, "Process list"));
        links.Add(Link.Create($"{baseUrl}{BasePath}/jobs", "http://www.opengis.net/def/rel/ogc/1.0/job-list", MediaTypes.Json, "Job list"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Processes",
            Description = "OGC API Processes adapter over the Honua canonical geoprocessing runtime.",
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            landingPage,
            OgcProcessesJsonContext.Default.LandingPage,
            outputFormat,
            "Landing page");
    }

    private static IResult GetConformance(
        HttpContext context,
        string? f,
        ILogger<OgcProcessesEndpointsLog> logger)
    {
        OgcProcessesLog.ConformanceRequested(logger);

        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ConformanceClasses,
            Links = OgcCoreMetadataUtilities.BuildConformanceLinks(
                context,
                $"{BasePath}/conformance",
                outputFormat)
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            conformance,
            OgcProcessesJsonContext.Default.ConformanceDeclaration,
            outputFormat,
            "Conformance");
    }

    private static async Task<IResult> GetOpenApiSpec(
        HttpContext context,
        string? f,
        [FromServices] IWebHostEnvironment environment)
    {
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Processes",
            "description": "OGC API Processes adapter over the Honua canonical geoprocessing runtime.",
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
            "ogc-processes-openapi.json",
            fallbackSpec);
    }

}

/// <summary>
/// Logging category for OGC Processes endpoints.
/// </summary>
internal sealed class OgcProcessesEndpointsLog;
