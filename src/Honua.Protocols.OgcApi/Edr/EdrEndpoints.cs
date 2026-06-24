// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Edr.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Edr;

/// <summary>
/// OGC API - Environmental Data Retrieval (EDR) endpoints (#1757). Exposes
/// <c>position</c> (point time-series) and <c>cube</c> (area/subset) queries over the
/// registered coverage/datacube collections, returning CoverageJSON.
/// </summary>
public static class EdrEndpoints
{
    /// <summary>Maps the OGC API - EDR endpoints.</summary>
    public static void MapEdrEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/edr").WithTags("OGC API - EDR");

        group.MapGet(string.Empty, GetLandingPage)
            .WithDisplayName("EDR API Landing Page")
            .WithName("GetEdrLandingPage")
            .WithSummary("Get OGC API - EDR landing page")
            .Produces<EdrLandingPage>(StatusCodes.Status200OK, MediaTypes.Json);

        group.MapGet("/conformance", GetConformance)
            .WithDisplayName("EDR API Conformance")
            .WithName("GetEdrConformance")
            .WithSummary("Get OGC API - EDR conformance classes")
            .Produces<EdrConformance>(StatusCodes.Status200OK, MediaTypes.Json);

        group.MapGet("/collections", GetCollections)
            .WithDisplayName("EDR Collections")
            .WithName("GetEdrCollections")
            .WithSummary("List OGC API - EDR collections")
            .Produces<EdrCollections>(StatusCodes.Status200OK, MediaTypes.Json);

        group.MapGet("/collections/{collectionId}", GetCollection)
            .WithDisplayName("EDR Collection")
            .WithName("GetEdrCollection")
            .WithSummary("Get OGC API - EDR collection metadata")
            .Produces<EdrCollection>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/collections/{collectionId}/position", GetPosition)
            .WithDisplayName("EDR Position Query")
            .WithName("GetEdrPosition")
            .WithSummary("EDR position (point time-series) query")
            .WithDescription("Returns a CoverageJSON time-series at coords=POINT(lon lat). Optional: datetime, parameter-name.")
            .Produces(StatusCodes.Status200OK, contentType: "application/prs.coveragejson+json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/collections/{collectionId}/cube", GetCube)
            .WithDisplayName("EDR Cube Query")
            .WithName("GetEdrCube")
            .WithSummary("EDR cube (area/subset) query")
            .WithDescription("Returns a CoverageJSON grid subset for bbox=minLon,minLat,maxLon,maxLat. Optional: datetime, parameter-name, resolution-x.")
            .Produces(StatusCodes.Status200OK, contentType: "application/prs.coveragejson+json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static IResult GetLandingPage(HttpContext context)
        => EdrHandler.GetLandingPage(context);

    private static IResult GetConformance()
        => EdrHandler.GetConformance();

    private static Task<IResult> GetCollections(HttpContext context, EdrHandler handler)
        => handler.GetCollectionsAsync(context, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetCollection(string collectionId, HttpContext context, EdrHandler handler)
        => handler.GetCollectionAsync(context, collectionId, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetPosition(string collectionId, HttpContext context, EdrHandler handler)
        => handler.GetPositionAsync(context, collectionId, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> GetCube(string collectionId, HttpContext context, EdrHandler handler)
        => handler.GetCubeAsync(context, collectionId, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));
}
