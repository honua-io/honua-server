// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Styling;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Mvc;

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
        [FromServices] ILayerStyleService styleService,
        CancellationToken cancellationToken)
    {
        if (layerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer id '{layerId}' is invalid.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var snapshot = await styleService.GetStyleAsync(layerValidation.Resource, layerId, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {layerId} not found.");
        }

        var response = BuildResponse(snapshot, unsupportedSymbolizers: null);

        var payload = ApiResponse<LayerStyleResponse>.CreateSuccess(response);
        return Results.Json(payload, LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);
    }

    private static async Task<IResult> HandleUpdateLayerStyle(
        int layerId,
        LayerStyleUpdateRequest request,
        HttpContext context,
        [FromServices] ILayerStyleService styleService,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        if (layerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer id '{layerId}' is invalid.");
        }

        if (!string.IsNullOrEmpty(request.ChangeSummary) && request.ChangeSummary.Length > 1000)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Change summary must be 1000 characters or fewer.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            scope: AccessScope.Write,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var result = await styleService.UpdateStyleAsync(
                layerValidation.Resource,
                layerId,
                request.MapLibreStyle,
                request.DrawingInfo,
                request.ChangedBy,
                request.ChangeSummary,
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

        var response = BuildResponse(result.Style, result.UnsupportedSymbolizers);

        var payload = ApiResponse<LayerStyleResponse>.CreateSuccess(response);
        return Results.Json(payload, LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);
    }

    private static LayerStyleResponse BuildResponse(
        LayerStyleSnapshot snapshot,
        IReadOnlyList<Honua.Core.Features.Styling.Domain.UnsupportedSymbolizerInfo>? unsupportedSymbolizers)
    {
        return new LayerStyleResponse
        {
            MapLibreStyle = snapshot.MapLibreStyle,
            DrawingInfo = snapshot.DrawingInfo,
            StyleVersion = snapshot.StyleVersion,
            RevisedAt = snapshot.RevisedAt,
            RevisedBy = snapshot.RevisedBy,
            ChangeSummary = snapshot.ChangeSummary,
            UnsupportedSymbolizers = unsupportedSymbolizers
        };
    }
}
