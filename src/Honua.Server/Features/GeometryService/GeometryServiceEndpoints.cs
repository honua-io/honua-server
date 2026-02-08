// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.GeometryService.Models;
using Honua.Server.Features.GeometryService.Services;

namespace Honua.Server.Features.GeometryService;

/// <summary>
/// Maps geometry service REST endpoints for buffer, simplify, and project operations.
/// </summary>
internal static class GeometryServiceEndpoints
{
    /// <summary>
    /// Maps geometry service endpoints under /rest/services/geometry.
    /// </summary>
    public static IEndpointRouteBuilder MapGeometryServiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/rest/services/geometry/buffer", (Delegate)HandleBuffer)
            .WithDisplayName("Geometry Service Buffer")
            .WithName("GeometryServiceBuffer")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/simplify", (Delegate)HandleSimplify)
            .WithDisplayName("Geometry Service Simplify")
            .WithName("GeometryServiceSimplify")
            .WithTags("GeometryService");

        endpoints.MapPost("/rest/services/geometry/project", (Delegate)HandleProject)
            .WithDisplayName("Geometry Service Project")
            .WithName("GeometryServiceProject")
            .WithTags("GeometryService");

        return endpoints;
    }

    private static async Task<IResult> HandleBuffer(
        HttpContext context,
        BufferRequest request,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleBufferAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleSimplify(
        HttpContext context,
        SimplifyRequest request,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleSimplifyAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleProject(
        HttpContext context,
        ProjectRequest request,
        GeometryServiceHandler handler)
    {
        var ct = context.RequestAborted;
        return await handler.HandleProjectAsync(request, ct).ConfigureAwait(false);
    }
}
