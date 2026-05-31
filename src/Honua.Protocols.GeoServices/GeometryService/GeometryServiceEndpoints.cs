// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.GeometryService;

/// <summary>
/// Maps geometry service REST endpoints under the canonical GeometryServer route.
/// Supports both GET and POST for each operation per ArcGIS specification.
/// </summary>
internal static class GeometryServiceEndpoints
{
    private const string GeometryRoutePrefix = "/rest/services/Utilities/Geometry/GeometryServer";

    /// <summary>
    /// Maps geometry service endpoints under the canonical GeometryServer route.
    /// </summary>
    public static IEndpointRouteBuilder MapGeometryServiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Esri REST clients probe the GeometryServer root for currentVersion and
        // service description before invoking operations.
        endpoints.MapGet(GeometryRoutePrefix, HandleServiceInfo)
            .WithDisplayName("Geometry Service Info")
            .WithName("GeometryServiceInfo")
            .WithTags("GeometryService");

        // Esri clients hydrate the GeometryServer descriptor by POSTing
        // {"f":"json"}; mirror the GET root so discovery succeeds. Anonymous by
        // design like the other GeometryServer POST companions in this file.
        endpoints.MapPost(GeometryRoutePrefix, HandleServiceInfo)
            .WithDisplayName("Geometry Service Info (POST)")
            .WithName("GeometryServiceInfoPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (GET)")
            .WithName("GeometryServiceBufferGet")
            .WithTags("GeometryService");

        // PUBLIC by design (#1144): GeoServices geometry helpers are stateless
        // mathematical operations on inline coordinates with no data
        // mutation. POSTs mirror the GET form (used when payload exceeds URL
        // limits). Marked AllowAnonymous so the audit guard records the
        // intentional decision. Applied to every POST in this file.
        endpoints.MapPost($"{GeometryRoutePrefix}/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (POST)")
            .WithName("GeometryServiceBufferPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (GET)")
            .WithName("GeometryServiceSimplifyGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (POST)")
            .WithName("GeometryServiceSimplifyPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (GET)")
            .WithName("GeometryServiceProjectGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (POST)")
            .WithName("GeometryServiceProjectPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (GET)")
            .WithName("GeometryServiceIntersectGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (POST)")
            .WithName("GeometryServiceIntersectPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (GET)")
            .WithName("GeometryServiceUnionGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (POST)")
            .WithName("GeometryServiceUnionPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (GET)")
            .WithName("GeometryServiceClipGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (POST)")
            .WithName("GeometryServiceClipPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (GET)")
            .WithName("GeometryServiceDifferenceGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (POST)")
            .WithName("GeometryServiceDifferencePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/areasAndLengths", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Areas And Lengths (GET)")
            .WithName("GeometryServiceAreasAndLengthsGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/areasAndLengths", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Areas And Lengths (POST)")
            .WithName("GeometryServiceAreasAndLengthsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/lengths", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Lengths (GET)")
            .WithName("GeometryServiceLengthsGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/lengths", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Lengths (POST)")
            .WithName("GeometryServiceLengthsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/distance", (Delegate)HandleDistance)
            .WithDisplayName("Geometry Service Distance (GET)")
            .WithName("GeometryServiceDistanceGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/distance", (Delegate)HandleDistance)
            .WithDisplayName("Geometry Service Distance (POST)")
            .WithName("GeometryServiceDistancePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/relation", (Delegate)HandleRelation)
            .WithDisplayName("Geometry Service Relation (GET)")
            .WithName("GeometryServiceRelationGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/relation", (Delegate)HandleRelation)
            .WithDisplayName("Geometry Service Relation (POST)")
            .WithName("GeometryServiceRelationPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/densify", (Delegate)HandleDensify)
            .WithDisplayName("Geometry Service Densify (GET)")
            .WithName("GeometryServiceDensifyGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/densify", (Delegate)HandleDensify)
            .WithDisplayName("Geometry Service Densify (POST)")
            .WithName("GeometryServiceDensifyPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/convexHull", (Delegate)HandleConvexHull)
            .WithDisplayName("Geometry Service Convex Hull (GET)")
            .WithName("GeometryServiceConvexHullGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/convexHull", (Delegate)HandleConvexHull)
            .WithDisplayName("Geometry Service Convex Hull (POST)")
            .WithName("GeometryServiceConvexHullPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/generalize", (Delegate)HandleGeneralize)
            .WithDisplayName("Geometry Service Generalize (GET)")
            .WithName("GeometryServiceGeneralizeGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/generalize", (Delegate)HandleGeneralize)
            .WithDisplayName("Geometry Service Generalize (POST)")
            .WithName("GeometryServiceGeneralizePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        endpoints.MapGet($"{GeometryRoutePrefix}/labelPoints", (Delegate)HandleLabelPoints)
            .WithDisplayName("Geometry Service Label Points (GET)")
            .WithName("GeometryServiceLabelPointsGet")
            .WithTags("GeometryService");

        endpoints.MapPost($"{GeometryRoutePrefix}/labelPoints", (Delegate)HandleLabelPoints)
            .WithDisplayName("Geometry Service Label Points (POST)")
            .WithName("GeometryServiceLabelPointsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        return endpoints;
    }

    // Minimal ArcGIS REST service descriptor for GeometryServer. The schema matches
    // Esri's documented response so probing clients (ArcGIS Pro, JS API) can complete
    // their discovery handshake before invoking buffer/simplify/etc.
    private const string GeometryServerInfoJson =
        "{\"currentVersion\":11.1,"
        + "\"serviceDescription\":\"Honua Geometry Service — buffer, simplify, project, intersect, union, clip, difference, areasAndLengths, lengths, distance, relation, densify, convexHull, generalize, labelPoints.\","
        + "\"maxBufferCount\":1000,"
        + "\"maxSimplifyCount\":1000,"
        + "\"resampled\":true}";

    private static IResult HandleServiceInfo()
        => Results.Text(GeometryServerInfoJson, "application/json");

    private static async Task<IResult> HandleBuffer(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleBufferAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleSimplify(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleSimplifyAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleProject(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleProjectAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleIntersect(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleIntersectAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleUnion(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleUnionAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleClip(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleClipAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDifference(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleDifferenceAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleArea(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleAreaAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleLength(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleLengthAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDistance(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleDistanceAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleRelation(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleRelationAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDensify(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleDensifyAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleConvexHull(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleConvexHullAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleGeneralize(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleGeneralizeAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleLabelPoints(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleLabelPointsAsync(context, ct).ConfigureAwait(false);
    }
}
