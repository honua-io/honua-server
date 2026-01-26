// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for layer style metadata management.
/// </summary>
internal static class AdminLayerStyleEndpoints
{
    /// <summary>
    /// Maps layer style endpoints to the admin metadata API group.
    /// </summary>
    public static void MapAdminLayerStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Styles")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/style", HandleGetLayerStyle)
            .WithName("GetAdminLayerStyle")
            .WithSummary("Get layer style metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPut("/{layerId:int}/style", HandleUpdateLayerStyle)
            .WithName("UpdateAdminLayerStyle")
            .WithSummary("Update layer style metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<IResult> HandleGetLayerStyle(
        int layerId,
        HttpContext context,
        IResourceValidator resourceValidator,
        ILayerStyleService styleService,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found.";
            return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
        }

        var snapshot = await styleService.GetStyleAsync(layerResult.Resource, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {layerId} not found.");
        }

        var response = new LayerStyleResponse
        {
            MapLibreStyle = snapshot.MapLibreStyle,
            DrawingInfo = snapshot.DrawingInfo
        };

        var payload = ApiResponse<LayerStyleResponse>.CreateSuccess(response);
        return Results.Json(payload, LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);
    }

    private static async Task<IResult> HandleUpdateLayerStyle(
        int layerId,
        LayerStyleUpdateRequest request,
        HttpContext context,
        IResourceValidator resourceValidator,
        ILayerStyleService styleService,
        OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found.";
            return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
        }

        var result = await styleService.UpdateStyleAsync(
                layerResult.Resource,
                request.MapLibreStyle,
                request.DrawingInfo,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == LayerStyleUpdateStatus.Invalid)
        {
            var message = result.ErrorMessage ?? "Invalid layer style payload.";
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, message);
        }

        if (result.Status == LayerStyleUpdateStatus.NotFound)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {layerId} not found.");
        }

        if (result.Style == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Failed to update layer style.");
        }

        await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken).ConfigureAwait(false);

        var response = new LayerStyleResponse
        {
            MapLibreStyle = result.Style.MapLibreStyle,
            DrawingInfo = result.Style.DrawingInfo
        };

        var payload = ApiResponse<LayerStyleResponse>.CreateSuccess(response);
        return Results.Json(payload, LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);
    }
}
