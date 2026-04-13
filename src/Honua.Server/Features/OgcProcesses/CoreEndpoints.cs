// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Ogc.Common;

namespace Honua.Server.Features.OgcProcesses;

/// <summary>
/// OGC API Processes core metadata endpoints (landing page, conformance).
/// </summary>
internal static class CoreEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    private static readonly ImmutableArray<string> ConformanceClasses = ImmutableArray.Create(
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list",
        "http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json");

    public static void MapOgcProcessesCoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(BasePath, GetLandingPage)
            .WithTags(Tag)
            .WithName("OgcProcessesLandingPage")
            .WithSummary("OGC API Processes landing page")
            .Produces<LandingPage>()
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/conformance", GetConformance)
            .WithTags(Tag)
            .WithName("OgcProcessesConformance")
            .WithSummary("OGC API Processes conformance declaration")
            .Produces<ConformanceDeclaration>()
            .ExcludeFromDescription();
    }

    private static IResult GetLandingPage(HttpContext context, ILogger<OgcProcessesEndpointsLog> logger)
    {
        OgcProcessesLog.LandingPageRequested(logger);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var links = ImmutableArray.Create(
            Link.Create($"{baseUrl}{BasePath}", RelationTypes.Self, MediaTypes.Json, "This document"),
            Link.Create($"{baseUrl}{BasePath}/conformance", RelationTypes.Conformance, MediaTypes.Json, "Conformance declaration"),
            Link.Create($"{baseUrl}{BasePath}/processes", "http://www.opengis.net/def/rel/ogc/1.0/processes", MediaTypes.Json, "Process list"),
            Link.Create($"{baseUrl}{BasePath}/jobs", "http://www.opengis.net/def/rel/ogc/1.0/job-list", MediaTypes.Json, "Job list"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Processes",
            Description = "OGC API Processes adapter over the Honua canonical geoprocessing runtime.",
            Links = links
        };

        return Results.Json(landingPage, OgcProcessesJsonContext.Default.LandingPage, MediaTypes.Json);
    }

    private static IResult GetConformance(HttpContext context, ILogger<OgcProcessesEndpointsLog> logger)
    {
        OgcProcessesLog.ConformanceRequested(logger);

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ConformanceClasses
        };

        return Results.Json(conformance, OgcProcessesJsonContext.Default.ConformanceDeclaration, MediaTypes.Json);
    }

}

/// <summary>
/// Logging category for OGC Processes endpoints.
/// </summary>
internal sealed class OgcProcessesEndpointsLog;
