// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.GeometryService.Services;

namespace Honua.Server.Features.GeometryService;

/// <summary>
/// Maps geometry service REST endpoints for buffer, simplify, and project operations.
/// Supports both GET and POST for each operation per ArcGIS specification.
/// </summary>
internal static class GeometryServiceEndpoints
{
    /// <summary>
    /// Maps geometry service endpoints under /rest/services/geometry.
    /// </summary>
    public static IEndpointRouteBuilder MapGeometryServiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/rest/services/geometry/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (GET)")
            .WithName("GeometryServiceBufferGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer (POST)")
            .WithName("GeometryServiceBufferPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (GET)")
            .WithName("GeometryServiceSimplifyGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify (POST)")
            .WithName("GeometryServiceSimplifyPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (GET)")
            .WithName("GeometryServiceProjectGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project (POST)")
            .WithName("GeometryServiceProjectPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (GET)")
            .WithName("GeometryServiceIntersectGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/intersect", (Delegate)HandleIntersect)
            .WithDisplayName("Geometry Service Intersect (POST)")
            .WithName("GeometryServiceIntersectPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (GET)")
            .WithName("GeometryServiceUnionGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/union", (Delegate)HandleUnion)
            .WithDisplayName("Geometry Service Union (POST)")
            .WithName("GeometryServiceUnionPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (GET)")
            .WithName("GeometryServiceClipGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/clip", (Delegate)HandleClip)
            .WithDisplayName("Geometry Service Clip (POST)")
            .WithName("GeometryServiceClipPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (GET)")
            .WithName("GeometryServiceDifferenceGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/difference", (Delegate)HandleDifference)
            .WithDisplayName("Geometry Service Difference (POST)")
            .WithName("GeometryServiceDifferencePost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/area", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Area (GET)")
            .WithName("GeometryServiceAreaGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/area", (Delegate)HandleArea)
            .WithDisplayName("Geometry Service Area (POST)")
            .WithName("GeometryServiceAreaPost")
            .WithTags("GeometryService");

        endpoints.MapGet("/rest/services/geometry/length", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Length (GET)")
            .WithName("GeometryServiceLengthGet")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/length", (Delegate)HandleLength)
            .WithDisplayName("Geometry Service Length (POST)")
            .WithName("GeometryServiceLengthPost")
            .WithTags("GeometryService");

        return endpoints;
    }

    private static async Task<IResult> HandleBuffer(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleBufferAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleSimplify(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleSimplifyAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleProject(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleProjectAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleIntersect(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleIntersectAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleUnion(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleUnionAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleClip(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleClipAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDifference(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleDifferenceAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleArea(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleAreaAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleLength(
        HttpContext context,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleLengthAsync(context, ct).ConfigureAwait(false);
    }
}
