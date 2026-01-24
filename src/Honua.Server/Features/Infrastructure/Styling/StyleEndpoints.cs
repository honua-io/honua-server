// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class StyleEndpoints
{
    public static IEndpointRouteBuilder MapStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/styles/{layerId:int}.json", HandleGetStyle)
            .WithDisplayName("Get MapLibre Style")
            .WithName("GetMapLibreStyle")
            .WithSummary("Get MapLibre style for a layer")
            .WithDescription("Returns a MapLibre style JSON document for the requested layer")
            .WithTags("Styles")
            .CacheOutput("LayerStyle")
            .WithETag()
            .Produces<JsonElement>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> HandleGetStyle(
        int layerId,
        HttpContext context,
        ILayerStyleService styleService,
        CancellationToken cancellationToken)
    {
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        var snapshot = await styleService.GetStyleAsync(layer, cancellationToken);
        var styleElement = snapshot?.MapLibreStyle;

        if (!styleElement.HasValue || styleElement.Value.ValueKind == JsonValueKind.Null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style for layer {layerId} not found.");
        }

        return Results.Json(styleElement.Value, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
    }
}
