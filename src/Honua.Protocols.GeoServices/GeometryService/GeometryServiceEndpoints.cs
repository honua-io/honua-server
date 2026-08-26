// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;

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

        // Every GeometryServer route is a stateless mathematical operation over
        // inline coordinates. Group metadata makes that tenant-independent
        // contract explicit without broad path-based exemptions in middleware.
        var geometryGroup = endpoints.MapGroup(string.Empty)
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance);

        // Esri REST clients probe the GeometryServer root for the service description
        // and capabilities before invoking operations.
        geometryGroup.MapGet(GeometryRoutePrefix, HandleServiceInfo)
            .WithDisplayName("Geometry Service Info")
            .WithName("GeometryServiceInfo")
            .WithTags("GeometryService");

        // Esri clients hydrate the GeometryServer descriptor by POSTing
        // {"f":"json"}; mirror the GET root so discovery succeeds. Anonymous by
        // design like the other GeometryServer POST companions in this file.
        geometryGroup.MapPost(GeometryRoutePrefix, HandleServiceInfo)
            .WithDisplayName("Geometry Service Info (POST)")
            .WithName("GeometryServiceInfoPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (GET)")
            .WithName("GeometryServiceBufferGet")
            .WithTags("GeometryService");

        // PUBLIC by design (#1144): GeoServices geometry helpers are stateless
        // mathematical operations on inline coordinates with no data
        // mutation. POSTs mirror the GET form (used when payload exceeds URL
        // limits). Marked AllowAnonymous so the audit guard records the
        // intentional decision. Applied to every POST in this file.
        geometryGroup.MapPost($"{GeometryRoutePrefix}/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (POST)")
            .WithName("GeometryServiceBufferPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (GET)")
            .WithName("GeometryServiceSimplifyGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (POST)")
            .WithName("GeometryServiceSimplifyPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (GET)")
            .WithName("GeometryServiceProjectGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (POST)")
            .WithName("GeometryServiceProjectPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (GET)")
            .WithName("GeometryServiceIntersectGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (POST)")
            .WithName("GeometryServiceIntersectPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (GET)")
            .WithName("GeometryServiceUnionGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (POST)")
            .WithName("GeometryServiceUnionPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (GET)")
            .WithName("GeometryServiceClipGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (POST)")
            .WithName("GeometryServiceClipPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (GET)")
            .WithName("GeometryServiceDifferenceGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (POST)")
            .WithName("GeometryServiceDifferencePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/areasAndLengths", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Areas And Lengths (GET)")
            .WithName("GeometryServiceAreasAndLengthsGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/areasAndLengths", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Areas And Lengths (POST)")
            .WithName("GeometryServiceAreasAndLengthsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/lengths", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Lengths (GET)")
            .WithName("GeometryServiceLengthsGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/lengths", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Lengths (POST)")
            .WithName("GeometryServiceLengthsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/distance", (Delegate)HandleDistance)
            .WithDisplayName("Geometry Service Distance (GET)")
            .WithName("GeometryServiceDistanceGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/distance", (Delegate)HandleDistance)
            .WithDisplayName("Geometry Service Distance (POST)")
            .WithName("GeometryServiceDistancePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/relation", (Delegate)HandleRelation)
            .WithDisplayName("Geometry Service Relation (GET)")
            .WithName("GeometryServiceRelationGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/relation", (Delegate)HandleRelation)
            .WithDisplayName("Geometry Service Relation (POST)")
            .WithName("GeometryServiceRelationPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/densify", (Delegate)HandleDensify)
            .WithDisplayName("Geometry Service Densify (GET)")
            .WithName("GeometryServiceDensifyGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/densify", (Delegate)HandleDensify)
            .WithDisplayName("Geometry Service Densify (POST)")
            .WithName("GeometryServiceDensifyPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/convexHull", (Delegate)HandleConvexHull)
            .WithDisplayName("Geometry Service Convex Hull (GET)")
            .WithName("GeometryServiceConvexHullGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/convexHull", (Delegate)HandleConvexHull)
            .WithDisplayName("Geometry Service Convex Hull (POST)")
            .WithName("GeometryServiceConvexHullPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/generalize", (Delegate)HandleGeneralize)
            .WithDisplayName("Geometry Service Generalize (GET)")
            .WithName("GeometryServiceGeneralizeGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/generalize", (Delegate)HandleGeneralize)
            .WithDisplayName("Geometry Service Generalize (POST)")
            .WithName("GeometryServiceGeneralizePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/labelPoints", (Delegate)HandleLabelPoints)
            .WithDisplayName("Geometry Service Label Points (GET)")
            .WithName("GeometryServiceLabelPointsGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/labelPoints", (Delegate)HandleLabelPoints)
            .WithDisplayName("Geometry Service Label Points (POST)")
            .WithName("GeometryServiceLabelPointsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/cut", (Delegate)HandleCut)
            .WithDisplayName("Geometry Service Cut (GET)")
            .WithName("GeometryServiceCutGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/cut", (Delegate)HandleCut)
            .WithDisplayName("Geometry Service Cut (POST)")
            .WithName("GeometryServiceCutPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/trimExtend", (Delegate)HandleTrimExtend)
            .WithDisplayName("Geometry Service Trim Extend (GET)")
            .WithName("GeometryServiceTrimExtendGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/trimExtend", (Delegate)HandleTrimExtend)
            .WithDisplayName("Geometry Service Trim Extend (POST)")
            .WithName("GeometryServiceTrimExtendPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/offset", (Delegate)HandleOffset)
            .WithDisplayName("Geometry Service Offset (GET)")
            .WithName("GeometryServiceOffsetGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/offset", (Delegate)HandleOffset)
            .WithDisplayName("Geometry Service Offset (POST)")
            .WithName("GeometryServiceOffsetPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/autoComplete", (Delegate)HandleAutoComplete)
            .WithDisplayName("Geometry Service Auto Complete (GET)")
            .WithName("GeometryServiceAutoCompleteGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/autoComplete", (Delegate)HandleAutoComplete)
            .WithDisplayName("Geometry Service Auto Complete (POST)")
            .WithName("GeometryServiceAutoCompletePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/reshape", (Delegate)HandleReshape)
            .WithDisplayName("Geometry Service Reshape (GET)")
            .WithName("GeometryServiceReshapeGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/reshape", (Delegate)HandleReshape)
            .WithDisplayName("Geometry Service Reshape (POST)")
            .WithName("GeometryServiceReshapePost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/findTransformations", (Delegate)HandleFindTransformations)
            .WithDisplayName("Geometry Service Find Transformations (GET)")
            .WithName("GeometryServiceFindTransformationsGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/findTransformations", (Delegate)HandleFindTransformations)
            .WithDisplayName("Geometry Service Find Transformations (POST)")
            .WithName("GeometryServiceFindTransformationsPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/toGeoCoordinateString", (Delegate)HandleToGeoCoordinateString)
            .WithDisplayName("Geometry Service To Geo Coordinate String (GET)")
            .WithName("GeometryServiceToGeoCoordinateStringGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/toGeoCoordinateString", (Delegate)HandleToGeoCoordinateString)
            .WithDisplayName("Geometry Service To Geo Coordinate String (POST)")
            .WithName("GeometryServiceToGeoCoordinateStringPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        geometryGroup.MapGet($"{GeometryRoutePrefix}/fromGeoCoordinateString", (Delegate)HandleFromGeoCoordinateString)
            .WithDisplayName("Geometry Service From Geo Coordinate String (GET)")
            .WithName("GeometryServiceFromGeoCoordinateStringGet")
            .WithTags("GeometryService");

        geometryGroup.MapPost($"{GeometryRoutePrefix}/fromGeoCoordinateString", (Delegate)HandleFromGeoCoordinateString)
            .WithDisplayName("Geometry Service From Geo Coordinate String (POST)")
            .WithName("GeometryServiceFromGeoCoordinateStringPost")
            .WithTags("GeometryService")
            .AllowAnonymous();

        return endpoints;
    }

    // Minimal ArcGIS REST service descriptor for GeometryServer. The schema matches
    // Esri's documented response so probing clients (ArcGIS Pro, JS API) can complete
    // their discovery handshake before invoking buffer/simplify/etc.
    private const string GeometryServerInfoJson =
        // No ArcGIS Server version (currentVersion) is advertised — Honua does not impersonate
        // a specific ArcGIS Server release (guarded by NoArcGisServerVersionTests).
        "{\"serviceDescription\":\"Honua Geometry Service — buffer, simplify, project, intersect, union, clip, difference, areasAndLengths, lengths, distance, relation, densify, convexHull, generalize, labelPoints, cut, trimExtend, offset, autoComplete, reshape, findTransformations, toGeoCoordinateString, fromGeoCoordinateString.\","
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

    private static async Task<IResult> HandleCut(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleCutAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleTrimExtend(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleTrimExtendAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleOffset(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleOffsetAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleAutoComplete(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleAutoCompleteAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleReshape(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleReshapeAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleFindTransformations(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleFindTransformationsAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleToGeoCoordinateString(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleToGeoCoordinateStringAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleFromGeoCoordinateString(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        return await handler.HandleFromGeoCoordinateStringAsync(context, ct).ConfigureAwait(false);
    }
}
