// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Styling;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

// The storageLayerId style routes are deprecated aliases of the styleId OGC Styles surface and stay so existing clients keep working.
internal static class AdminLayerStyleEndpoints
{
    public static void MapAdminLayerStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Styles")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{storageLayerId:int}/style", HandleGetLayerStyle)
            .WithName("GetAdminLayerStyle")
            .WithSummary("[Deprecated] Get layer style metadata by storageLayerId")
            .WithDescription("DEPRECATED storageLayerId-keyed alias. Use the canonical styleId surface GET /ogc/styles/{styleId} (ADR-0048). Kept working for back-compat; emits advisory Deprecation/Sunset headers.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPut("/{storageLayerId:int}/style", HandleUpdateLayerStyle)
            .WithName("UpdateAdminLayerStyle")
            .WithSummary("[Deprecated] Update layer style metadata by storageLayerId")
            .WithDescription("DEPRECATED storageLayerId-keyed alias. Use the canonical styleId surface PUT /ogc/styles/{styleId} (ADR-0048). Kept working for back-compat; emits advisory Deprecation/Sunset headers.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<IResult> HandleGetLayerStyle(
        int storageLayerId,
        HttpContext context,
        [FromServices] ILayerStyleService styleService,
        CancellationToken cancellationToken)
    {
        // Advisory deprecation signaling for the storageLayerId-keyed admin alias.
        StyleDeprecationHeaders.Apply(context, "/ogc/styles");

        if (storageLayerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer id '{storageLayerId}' is invalid.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateStorageLayerWithAccessV2Async(
            context, storageLayerId, AuthorizationOperation.Query).ConfigureAwait(false);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var snapshot = await styleService.GetStyleAsync(layerValidation.Resource, storageLayerId, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Style for layer {storageLayerId} not found.");
        }

        var response = BuildResponse(snapshot, unsupportedSymbolizers: null);

        var payload = ApiResponse<LayerStyleResponse>.CreateSuccess(response);
        return Results.Json(payload, LayerStyleJsonContext.Default.ApiResponseLayerStyleResponse);
    }

    private static async Task<IResult> HandleUpdateLayerStyle(
        int storageLayerId,
        LayerStyleUpdateRequest request,
        HttpContext context,
        [FromServices] ILayerStyleService styleService,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        // Advisory deprecation signaling for the storageLayerId-keyed admin alias.
        StyleDeprecationHeaders.Apply(context, "/ogc/styles");

        if (storageLayerId < 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer id '{storageLayerId}' is invalid.");
        }

        if (!string.IsNullOrEmpty(request.ChangeSummary) && request.ChangeSummary.Length > 1000)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Change summary must be 1000 characters or fewer.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateStorageLayerWithAccessV2Async(
            context, storageLayerId, AuthorizationOperation.Admin).ConfigureAwait(false);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var result = await styleService.UpdateStyleAsync(
                layerValidation.Resource,
                storageLayerId,
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
                $"Style for layer {storageLayerId} not found.");
        }

        if (result.Style == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Failed to update layer style.");
        }

        await cacheInvalidator.InvalidateLayerAsync(null, storageLayerId, cancellationToken).ConfigureAwait(false);

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
