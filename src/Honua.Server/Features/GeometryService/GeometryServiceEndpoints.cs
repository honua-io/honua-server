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
}
